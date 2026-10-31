using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Munito;

/// <summary>OCR is exposed as a special model: POST /ocr, optional async polling via GET/DELETE /ocr/{request_id}.</summary>
public sealed partial class MunitoProvider
{
    private const string OcrToolName = "munito_ocr";
    private const string OcrAutoModel = "munito/ocr-auto";
    private const int MaxOcrPages = 1000;
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> OcrPassthroughOptions = new(StringComparer.Ordinal)
    {
        "pages", "tables", "images", "confidence_scores", "extract", "preferred_region"
    };

    private static bool IsOcrModel(string? model)
    {
        var normalized = NormalizeModelId(model);
        return normalized.Equals(OcrAutoModel, StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("ocr", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AIResponse> ExecuteOcrUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var model = NormalizeModelId(request.Model);
        if (request.ResponseFormat is not null && !IsTextResponseFormat(request.ResponseFormat))
            throw new ArgumentException("Munito OCR returns Markdown and does not support structured response formats.", nameof(request));

        var userMessage = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = userMessage?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Munito OCR requires a file in the latest user message.", nameof(request));

        var providerOptions = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        var options = ReadOcrOptions(providerOptions);
        var output = new List<AIOutputItem>();
        var pagesProcessed = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var file = NormalizeOcrFile(files[index], index);
            var payload = new JsonObject();
            if (!model.Equals(OcrAutoModel, StringComparison.OrdinalIgnoreCase))
                payload["model"] = model;
            payload["document"] = file.Document;
            foreach (var (name, value) in options.Body)
                payload[name] = value?.DeepClone();

            var result = await ProcessOcrFileAsync(payload, options, file.Filename, cancellationToken);
            var toolId = Guid.NewGuid().ToString("n");
            output.Add(new AIOutputItem
            {
                Type = "tool-call",
                Role = "assistant",
                Content = [new AIToolCallContentPart
                {
                    Type = "tool-call", ToolCallId = toolId, ToolName = OcrToolName, Title = "Munito OCR",
                    Input = new { model, filename = file.Filename, media_type = file.MediaType, file_index = index },
                    State = "output-available", ProviderExecuted = true,
                    Output = new CallToolResult { IsError = false, Content = [], StructuredContent = JsonSerializer.SerializeToElement(result) }
                }]
            });
            output.Add(CreateOcrMessage(result));
            pagesProcessed += (result["pages"] as JsonArray)?.Count ?? 0;
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{model}",
            Status = "completed",
            Output = new AIOutput { Items = output },
            Usage = new Dictionary<string, object?> { ["pages_processed"] = pagesProcessed },
            Metadata = new Dictionary<string, object?> { ["finishReason"] = "stop", ["munito.ocr.pages_processed"] = pagesProcessed }
        };
    }

    private static bool IsTextResponseFormat(object responseFormat)
        => JsonSerializer.SerializeToNode(responseFormat, JsonSerializerOptions.Web) is JsonObject format
           && format["type"]?.GetValue<string>() == "text";

    private sealed record OcrOptions(
        JsonObject Body, string? Region, string? ServiceTier, bool Async);

    private static OcrOptions ReadOcrOptions(JsonElement providerOptions)
    {
        var body = new JsonObject();
        string? region = null, tier = null;
        var async = false;
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return new(body, region, tier, async);

        foreach (var property in providerOptions.EnumerateObject())
        {
            switch (property.Name)
            {
                case "region":
                    region = NormalizeRegion(property.Value.GetString());
                    break;
                case "service_tier":
                    tier = property.Value.GetString();
                    if (tier is not ("priority" or "standard" or "flex"))
                        throw new ArgumentException("Munito service_tier must be priority, standard or flex.");
                    break;
                case "async":
                    async = property.Value.ValueKind == JsonValueKind.True;
                    break;
                case var name when OcrPassthroughOptions.Contains(name):
                    body[name] = JsonNode.Parse(property.Value.GetRawText());
                    break;
                default:
                    throw new ArgumentException($"Munito OCR does not support provider option '{property.Name}'.");
            }
        }

        ValidateOcrBody(body);
        return new(body, region, tier, async);
    }

    private static void ValidateOcrBody(JsonObject body)
    {
        if (body["pages"] is JsonNode pages)
        {
            var numbers = pages is JsonArray array
                ? array.Select(item => item is JsonValue value && value.TryGetValue<int>(out var number) ? number : -1).ToList()
                : throw new ArgumentException("Munito OCR pages must be an array of zero-based page numbers.");
            if (numbers.Count is 0 or > MaxOcrPages || numbers.Any(number => number < 0) || numbers.Distinct().Count() != numbers.Count)
                throw new ArgumentException($"Munito OCR pages must be 1 to {MaxOcrPages} unique, non-negative, zero-based numbers.");
        }

        if (body["tables"] is JsonObject tables)
        {
            RequireEnum(tables, "mode", "inline", "extracted");
            RequireEnum(tables, "format", "markdown", "html");
        }

        if (body["extract"] is JsonNode extract)
        {
            if (extract is not JsonArray extractArray
                || extractArray.Any(item => item?.GetValue<string>() is not ("header" or "footer" or "page_number" or "aside")))
                throw new ArgumentException("Munito OCR extract accepts header, footer, page_number and aside.");
        }

        if (body["preferred_region"] is JsonNode region && region.GetValue<string>() is not ("global" or "eu" or "us"))
            throw new ArgumentException("Munito OCR preferred_region must be global, eu or us.");
    }

    private static void RequireEnum(JsonObject value, string name, params string[] allowed)
    {
        if (value[name] is JsonNode node && !allowed.Contains(node.GetValue<string>()))
            throw new ArgumentException($"Munito OCR {name} must be one of: {string.Join(", ", allowed)}.");
    }

    private async Task<JsonObject> ProcessOcrFileAsync(
        JsonObject payload, OcrOptions options, string filename, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, ResolveUrl(options.Region, "ocr"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        if (options.ServiceTier is not null)
            message.Headers.TryAddWithoutValidation(ServiceTierHeader, options.ServiceTier);
        if (options.Async)
            message.Headers.TryAddWithoutValidation("Prefer", "respond-async");

        using var response = await _client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            return await PollOcrAsync(response, message.RequestUri!, filename, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateOcrException(filename, response, body);

        return ParseOcrObject(body);
    }

    private async Task<JsonObject> PollOcrAsync(
        HttpResponseMessage accepted, Uri requestUri, string filename, CancellationToken cancellationToken)
    {
        var baseUri = requestUri.IsAbsoluteUri ? requestUri : new Uri(_client.BaseAddress!, requestUri);
        var location = accepted.Headers.Location
            ?? throw new InvalidOperationException("Munito OCR accepted the request without a location header.");
        var pollUri = location.IsAbsoluteUri ? location : new Uri(baseUri, location);
        // The API key is only ever sent to Munito's own hosts.
        if (pollUri.Scheme != Uri.UriSchemeHttps
            || (pollUri.Host != new Uri(GlobalBaseUrl).Host && pollUri.Host != new Uri(EuBaseUrl).Host))
            throw new InvalidOperationException("Munito OCR returned a polling location outside Munito's API hosts.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OcrTimeout);
        var delay = TimeSpan.FromMilliseconds(500);
        try
        {
            while (true)
            {
                using var poll = new HttpRequestMessage(HttpMethod.Get, pollUri);
                using var response = await _client.SendAsync(poll, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw CreateOcrException(filename, response, body);

                var result = ParseOcrObject(body);
                switch (result["status"]?.GetValue<string>())
                {
                    case null or "completed":
                        return result;
                    case "running":
                        await Task.Delay(delay, timeout.Token);
                        delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 5000));
                        break;
                    case var status:
                        throw new InvalidOperationException($"Munito OCR for '{filename}' ended with status '{status}': {body}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            await CancelOcrAsync(pollUri);
            throw;
        }
    }

    // Best effort: stop billing for pages the caller no longer waits for.
    private async Task CancelOcrAsync(Uri pollUri)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Delete, pollUri);
            using var response = await _client.SendAsync(request, cleanup.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
        }
    }

    private static HttpRequestException CreateOcrException(string filename, HttpResponseMessage response, string body)
    {
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        var retryAfter = response.Headers.RetryAfter?.ToString();
        return new HttpRequestException(
            $"Munito OCR failed for '{filename}' ({(int)response.StatusCode})"
            + (requestId is null ? "" : $" [request {requestId}]")
            + (retryAfter is null ? "" : $" [Retry-After: {retryAfter}]")
            + $": {body}",
            null,
            response.StatusCode);
    }

    private static JsonObject ParseOcrObject(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject
                ?? throw new InvalidOperationException("Munito OCR returned an invalid JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Munito OCR returned an invalid JSON object.", exception);
        }
    }

    private static (string Filename, string MediaType, JsonObject Document) NormalizeOcrFile(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Munito OCR file {index + 1} must contain base64 data or an HTTPS URL.", nameof(file));

        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
        var mediaType = file.MediaType ?? "application/octet-stream";
        value = value.Trim();

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Munito only fetches public https URLs on port 443.
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Port != 443)
                throw new ArgumentException($"Munito OCR file {index + 1} must use a public https URL on port 443.", nameof(file));

            return (filename, mediaType, new JsonObject { ["type"] = "url", ["url"] = value });
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Munito OCR file {index + 1} must use a base64 data URL.", nameof(file));
            mediaType = value[5..value.IndexOf(';')];
            value = value[(comma + 1)..];
        }

        try { _ = Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new ArgumentException($"Munito OCR file {index + 1} contains invalid base64.", nameof(file), exception); }

        return (filename, mediaType, new JsonObject { ["type"] = "base64", ["data"] = value, ["filename"] = filename });
    }

    // The raw result stays available as the tool output; only Markdown text and inline images are lifted into the message.
    private static AIOutputItem CreateOcrMessage(JsonObject result)
    {
        var markdown = new List<string>();
        var content = new List<AIContentPart>();
        foreach (var page in (result["pages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var text = ReadString(page, "markdown") ?? ReadString(page, "text") ?? ReadString(page, "content");
            if (!string.IsNullOrEmpty(text))
                markdown.Add(text);

            foreach (var image in (page["images"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var raw = ReadString(image, "image_base64") ?? ReadString(image, "base64");
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var filename = ReadString(image, "id") ?? $"ocr-image-{Guid.NewGuid():n}.png";
                var mediaType = Path.GetExtension(filename).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png"
                };
                var dataUrl = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? raw : $"data:{mediaType};base64,{raw}";
                content.Add(new AIFileContentPart { Type = "file", Data = dataUrl, MediaType = mediaType, Filename = filename });
            }
        }

        content.Insert(0, new AITextContentPart { Type = "text", Text = string.Join("\n\n", markdown) });
        return new AIOutputItem { Type = "message", Role = "assistant", Content = content };
    }

    private static string? ReadString(JsonObject value, string name)
        => value[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    private async IAsyncEnumerable<AIStreamEvent> StreamOcrUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteOcrUnifiedAsync(request, cancellationToken);
        foreach (var item in response.Output?.Items ?? [])
        {
            var now = DateTimeOffset.UtcNow;
            if (item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault() is { } tool)
            {
                yield return OcrStreamEvent(tool.ToolCallId, "tool-input-available", new AIToolInputAvailableEventData
                {
                    ToolName = OcrToolName,
                    Title = tool.Title,
                    Input = tool.Input ?? throw new InvalidOperationException("Munito OCR response omitted its tool input."),
                    ProviderExecuted = true
                }, now);
                yield return OcrStreamEvent(tool.ToolCallId, "tool-output-available", new AIToolOutputAvailableEventData
                {
                    ToolName = OcrToolName,
                    Output = tool.Output ?? throw new InvalidOperationException("Munito OCR response omitted its tool output."),
                    ProviderExecuted = true
                }, now);
                continue;
            }

            var id = Guid.NewGuid().ToString("n");
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                yield return OcrStreamEvent(id, "text-start", new AITextStartEventData(), now);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return OcrStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, now);
                yield return OcrStreamEvent(id, "text-end", new AITextEndEventData(), now);
            }

            foreach (var image in (item.Content ?? []).OfType<AIFileContentPart>())
                yield return OcrStreamEvent(id, "file", new { mediaType = image.MediaType, filename = image.Filename, url = image.Data }, now);
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return OcrStreamEvent(Guid.NewGuid().ToString("n"), "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = completedAt.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? request.Model ?? "munito-ocr", completedAt, response.Usage)
        }, completedAt, response.Metadata);
    }

    private AIStreamEvent OcrStreamEvent(string? id, string type, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };
}
