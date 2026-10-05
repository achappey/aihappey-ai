using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.WebsearchAPI;

public partial class WebsearchAPIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var model = ResolveModel(request.Model);
        var options = GetProviderOptions(request);
        var content = new List<AIContentPart>();
        var sources = new List<AIOutputItem>();
        var results = new List<Dictionary<string, object?>>();

        if (model == "WebScraper")
        {
            var urls = GetScrapeUrls(request);
            if (urls.Count == 0)
                throw new InvalidOperationException("WebScraper requires at least one HTTPS file URL in the latest user message.");

            // Sequential execution preserves attachment order and avoids further credit usage after failure.
            foreach (var url in urls)
            {
                var payload = new Dictionary<string, object?>(options);
                payload.TryAdd("returnFormat", "markdown");
                payload["url"] = url;
                var result = await SendProviderRequestAsync("scrape", payload, cancellationToken);
                result.Metadata["requestedUrl"] = url;
                results.Add(result.Metadata);
                MapScrapeResult(result, url, payload["returnFormat"]?.ToString(), content, sources);
            }
        }
        else
        {
            var query = BuildSearchQuery(request);
            if (string.IsNullOrWhiteSpace(query))
                throw new InvalidOperationException("WebSearch requires non-empty text input.");
            var payload = new Dictionary<string, object?>(options);
            payload.TryAdd("includeAnswer", true);
            payload.TryAdd("includeContent", true);
            payload.TryAdd("contentFormat", "markdown");
            payload["query"] = query;
            var result = await SendProviderRequestAsync("ai-search", payload, cancellationToken);
            results.Add(result.Metadata);
            MapSearchResult(result, content, sources);
        }

        var providerMetadata = new Dictionary<string, object?> { ["results"] = results };
        // Keep the single search response directly accessible, including future provider fields.
        if (model == "WebSearch")
            foreach (var property in results[0])
                providerMetadata[property.Key] = property.Value;
        var metadata = new Dictionary<string, object?>
        {
            [GetIdentifier()] = providerMetadata,
            ["responses.completed_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["responses.temperature"] = request.Temperature,
            // Chat completions' mapper exposes extension fields from the raw response convention.
            ["chatcompletions.response.raw"] = JsonSerializer.SerializeToElement(new { provider_metadata = new { websearchapi = providerMetadata } })
        };
        var items = new List<AIOutputItem>
        {
            new()
            {
                Type = "message", Role = "assistant", Content = content,
                Metadata = new()
                {
                    ["chatcompletions.choice.finish_reason"] = "stop",
                    ["chatcompletions.message.provider_metadata"] = new Dictionary<string, object?> { [GetIdentifier()] = providerMetadata }
                }
            }
        };
        items.AddRange(sources);
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"{GetIdentifier()}/{model}", Status = "completed",
            Output = new AIOutput { Items = items }, Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The upstream endpoints are non-streaming. Buffer the response so a failed batch never signals success.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var id = request.Id ?? $"websearchapi_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow;
        var index = 0;
        foreach (var part in response.Output!.Items![0].Content!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partId = $"{id}_part_{index++}";
            var scoped = ScopedMetadata(part.Metadata);
            var textMetadata = scoped.ToDictionary(p => p.Key, p => (object)p.Value);
            if (part is AITextContentPart text)
            {
                yield return StreamEvent("text-start", partId, timestamp, new AITextStartEventData { ProviderMetadata = textMetadata }, response.Metadata);
                yield return StreamEvent("text-delta", partId, timestamp, new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = textMetadata }, response.Metadata);
                yield return StreamEvent("text-end", partId, timestamp, new AITextEndEventData { ProviderMetadata = textMetadata }, response.Metadata);
            }
            else if (part is AIFileContentPart file)
                yield return StreamEvent("file", partId, timestamp, new AIFileEventData
                { MediaType = file.MediaType!, Url = file.Data!.ToString()!, ProviderMetadata = scoped }, response.Metadata);
        }
        foreach (var source in response.Output.Items.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = (string)source.Metadata!["chatcompletions.source.url"]!;
            yield return StreamEvent("source-url", id, timestamp, new AISourceUrlEventData
            {
                SourceId = url, Url = url, Title = source.Metadata["chatcompletions.source.title"]?.ToString(),
                Type = "url_citation", ProviderMetadata = ScopedMetadata(source.Metadata)
            }, response.Metadata);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var finishMetadata = new Dictionary<string, object?>(response.Metadata!)
        {
            ["chatcompletions.stream.raw"] = JsonSerializer.SerializeToElement(new { provider_metadata = new { websearchapi = response.Metadata![GetIdentifier()] } })
        };
        yield return StreamEvent("finish", id, timestamp, new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, timestamp, temperature: request.Temperature,
                additionalProperties: new Dictionary<string, object?> { [GetIdentifier()] = response.Metadata![GetIdentifier()] })
        }, finishMetadata);
    }

    private string ResolveModel(string? model)
    {
        var slug = model?.Trim() ?? "";
        var prefix = GetIdentifier() + "/";
        if (slug.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            slug = slug[prefix.Length..];
        return slug.ToLowerInvariant() switch
        {
            "websearch" => "WebSearch",
            "webscraper" => "WebScraper",
            _ => throw new NotSupportedException($"Unsupported WebsearchAPI model '{model}'.")
        };
    }

    private Dictionary<string, object?> GetProviderOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var direct) == true)
            return ObjectOptions(direct);
        // Protocol mappers retain their original metadata under protocol-specific keys.
        foreach (var key in new[] { "chatcompletions.request.metadata", "messages.request.metadata",
            "chatcompletions.request.provider_metadata", "messages.request.provider_metadata" })
        {
            if (request.Metadata?.TryGetValue(key, out var value) != true || value is null)
                continue;
            var raw = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(GetIdentifier(), out var provider))
                return ObjectOptions(provider);
        }
        return [];
    }

    private static Dictionary<string, object?> ObjectOptions(object? value)
    {
        if (value is null)
            return [];
        var raw = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (raw.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("WebsearchAPI provider metadata must be a JSON object.");
        return raw.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private static AIInputItem? LatestUser(AIRequest request)
        => request.Input?.Items?.LastOrDefault(item => item.Type == "message"
            && string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));

    private static string BuildSearchQuery(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;
        var text = string.Join("\n", (LatestUser(request)?.Content ?? []).OfType<AITextContentPart>()
            .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(text) ? request.Instructions ?? "" : text;
    }

    private static List<string> GetScrapeUrls(AIRequest request)
        => (LatestUser(request)?.Content ?? []).OfType<AIFileContentPart>()
            .Select(file => file.Data switch
            {
                string text => text,
                Uri uri when uri.IsAbsoluteUri => uri.AbsoluteUri,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                // Chat Completions preserves non-text content as raw file-part JSON.
                JsonElement { ValueKind: JsonValueKind.Object } json => ChatFileUrl(json),
                _ => null
            })
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Select(url => url!.Trim()).ToList();

    private static string? ChatFileUrl(JsonElement part)
    {
        if (String(part, "type") != "file" || !part.TryGetProperty("file", out var file)
            || file.ValueKind != JsonValueKind.Object)
            return null;
        return String(file, "file_url") ?? String(file, "file_data");
    }

    private sealed record ProviderResult(JsonElement Raw, Dictionary<string, object?> Metadata, string? ImageData = null, string? MediaType = null);

    private async Task<ProviderResult> SendProviderRequestAsync(string endpoint, Dictionary<string, object?> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No WebsearchAPI API key.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = JsonContent.Create(payload, options: JsonSerializerOptions.Web) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var metadata = new Dictionary<string, object?>();
        foreach (var name in new[] { "X-Credits-Consumed", "X-Credits-Remaining" })
            if (response.Headers.TryGetValues(name, out var values))
                metadata[name] = string.Join(",", values);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (response.IsSuccessStatusCode && endpoint == "scrape" && mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return new(default, metadata, $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}", mediaType);
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"WebsearchAPI {endpoint} failed ({(int)response.StatusCode}): {body}", null, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var raw = document.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"WebsearchAPI {endpoint} returned a non-object response.");
        if ((raw.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            || (raw.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var status) && status >= 400))
            throw new HttpRequestException($"WebsearchAPI {endpoint} reported failure: {body}");
        foreach (var property in raw.EnumerateObject())
            metadata[property.Name] = property.Value.Clone();
        return new(raw, metadata);
    }

    private void MapScrapeResult(ProviderResult result, string requestedUrl, string? format,
        List<AIContentPart> content, List<AIOutputItem> sources)
    {
        if (result.ImageData is not null)
        {
            content.Add(TextPart(Link(requestedUrl, requestedUrl) + "\n\n", result.Metadata));
            content.Add(new AIFileContentPart { Type = "file", Data = result.ImageData, MediaType = result.MediaType, Metadata = Wrap(result.Metadata) });
            sources.Add(Source(requestedUrl, requestedUrl, result.Metadata));
            return;
        }
        if (!result.Raw.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("WebsearchAPI scrape response is missing its data object.");
        var url = HttpUrl(String(data, "url")) ?? requestedUrl;
        var title = String(data, "title") ?? url;
        var body = String(data, "content");
        var text = new StringBuilder().AppendLine(Link(title, url)).AppendLine();
        if (format is "screenshot" or "pageshot" && body is not null
            && (HttpUrl(body) is not null || body.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
        {
            content.Add(new AIFileContentPart
            {
                Type = "file", MediaType = body.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? body[5..body.IndexOf(';')] : "image/png",
                Data = body, Metadata = Wrap(result.Metadata)
            });
        }
        else if (body is not null)
            text.AppendLine(body);
        else if (data.TryGetProperty("content", out var structured))
            text.AppendLine(structured.GetRawText());
        AppendSummary(text, data, "links", "Links");
        AppendSummary(text, data, "images", "Images");
        content.Add(TextPart(text.ToString().TrimEnd() + "\n\n", result.Metadata));
        sources.Add(Source(url, title, result.Metadata));
    }

    private void MapSearchResult(ProviderResult result, List<AIContentPart> content, List<AIOutputItem> sources)
    {
        var text = new StringBuilder();
        var answer = String(result.Raw, "answer");
        if (!string.IsNullOrWhiteSpace(answer))
            text.AppendLine(answer).AppendLine();
        if (result.Raw.TryGetProperty("organic", out var organic) && organic.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in organic.EnumerateArray())
            {
                var url = HttpUrl(String(item, "url"));
                var title = String(item, "title") ?? url ?? "Result";
                text.AppendLine(url is null ? title : Link(title, url)).AppendLine();
                var body = String(item, "content") ?? String(item, "description");
                if (!string.IsNullOrWhiteSpace(body))
                    text.AppendLine(body).AppendLine();
                if (url is not null)
                    sources.Add(Source(url, title, item.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())));
            }
        }
        content.Add(TextPart(text.ToString().TrimEnd(), result.Metadata));
    }

    private static string? String(JsonElement raw, string property)
        => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? HttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    private static string Link(string title, string url)
        => $"[{title.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("\r", " ").Replace("\n", " ")}](<{url.Replace("<", "%3C").Replace(">", "%3E")}>)";

    private static void AppendSummary(StringBuilder text, JsonElement data, string property, string heading)
    {
        if (!data.TryGetProperty(property, out var summary) || summary.ValueKind != JsonValueKind.Object)
            return;
        text.AppendLine().AppendLine($"### {heading}");
        foreach (var item in summary.EnumerateObject())
            if (HttpUrl(item.Name) is { } url)
                text.AppendLine($"- {Link(item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() ?? url : url, url)}");
    }

    private Dictionary<string, object?> Wrap(Dictionary<string, object?> raw) => new() { [GetIdentifier()] = raw };

    private AITextContentPart TextPart(string text, Dictionary<string, object?> raw)
        => new() { Type = "text", Text = text, Metadata = Wrap(raw) };

    private AIOutputItem Source(string url, string title, Dictionary<string, object?> raw)
        => new()
        {
            Type = "source-url", Metadata = new()
            {
                ["chatcompletions.source.url"] = url, ["chatcompletions.source.title"] = title,
                ["chatcompletions.source.type"] = "url_citation", [GetIdentifier()] = raw
            }
        };

    private Dictionary<string, Dictionary<string, object>> ScopedMetadata(Dictionary<string, object?>? metadata)
        => new() { [GetIdentifier()] = metadata?.TryGetValue(GetIdentifier(), out var raw) == true
            ? ObjectOptions(raw).ToDictionary(p => p.Key, p => p.Value!) : [] };

    private AIStreamEvent StreamEvent(string type, string id, DateTimeOffset timestamp, object data, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data }
        };
}
