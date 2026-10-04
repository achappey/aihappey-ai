using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private static string ResolveModel(string? model)
    {
        var local = model?.Trim() ?? "auto";
        if (local.StartsWith("tavily/", StringComparison.OrdinalIgnoreCase))
            local = local["tavily/".Length..];
        local = local.ToLowerInvariant();
        if (local is not ("auto" or "mini" or "pro" or "crawl" or "extract"))
            throw new NotSupportedException($"Unsupported Tavily model '{model}'.");
        return local;
    }

    // Copy native options without a schema/allowlist, including future Tavily fields.
    private static Dictionary<string, object?> ReadOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("tavily", out var options) != true || options is null)
            return new(StringComparer.Ordinal);
        var json = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web);
        if (json.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tavily provider metadata must be a JSON object.");
        return json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    private static AIInputItem? LatestUserInput(AIRequest request)
        => request.Input?.Items?.LastOrDefault(item => item.Type == "message"
            && string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));

    private static string CurrentUserText(AIRequest request)
        => string.Join("\n", (LatestUserInput(request)?.Content ?? [])
            .OfType<AITextContentPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)))
            is { Length: > 0 } text ? text : request.Input?.Text ?? string.Empty;

    private static IEnumerable<string> ReadUrlValues(object? value)
    {
        if (value is null)
            yield break;
        var json = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (json.ValueKind == JsonValueKind.String)
        {
            if (!string.IsNullOrWhiteSpace(json.GetString()))
                yield return json.GetString()!.Trim();
        }
        else if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Tavily URL arrays must contain strings.");
                if (!string.IsNullOrWhiteSpace(item.GetString()))
                    yield return item.GetString()!.Trim();
            }
        }
        else if (json.ValueKind is not JsonValueKind.Null)
            throw new ArgumentException("Tavily URL metadata must be a string or an array of strings.");
    }

    private static IEnumerable<string> FileUrls(AIInputItem? item)
        => (item?.Content ?? []).OfType<AIFileContentPart>()
            .SelectMany(file => file.Data is string or JsonElement ? ReadUrlValues(file.Data) : [])
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https");

    private static string UrlKey(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant()
                + "/" + uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped)
            : url;

    private static List<string> ResolveUrls(AIRequest request, Dictionary<string, object?> options)
    {
        var candidates = ReadUrlValues(options.GetValueOrDefault("url"))
            .Concat(ReadUrlValues(options.GetValueOrDefault("urls")))
            .Concat(FileUrls(LatestUserInput(request)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return candidates.Where(url => seen.Add(UrlKey(url))).ToList();
    }

    private static string BuildPromptFromUnifiedRequest(AIRequest request)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            sections.Add($"system: {request.Instructions}");
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            sections.Add(request.Input.Text);
        foreach (var item in request.Input?.Items ?? [])
        {
            if (item.Type != "message" || item.Role is not ("user" or "assistant" or "system" or "developer"))
                continue;
            var parts = (item.Content ?? []).OfType<AITextContentPart>().Select(p => p.Text)
                .Concat(item.Role == "user" ? FileUrls(item) : [])
                .Where(text => !string.IsNullOrWhiteSpace(text));
            var text = string.Join("\n", parts);
            if (text.Length > 0)
                sections.Add($"{item.Role}: {text}");
        }
        return string.Join("\n\n", sections);
    }

    private static object? TryExtractOutputSchema(object? format)
    {
        if (format is null)
            return null;
        var schema = format.GetJSONSchema()?.JsonSchema?.Schema;
        if (schema is { ValueKind: JsonValueKind.Object })
            return schema.Value.Clone();
        var raw = JsonSerializer.SerializeToElement(format, JsonSerializerOptions.Web);
        if (raw.ValueKind != JsonValueKind.Object)
            return null;
        if (raw.TryGetProperty("json_schema", out var nested) && nested.ValueKind == JsonValueKind.Object)
            raw = nested;
        return raw.TryGetProperty("schema", out var direct) && direct.ValueKind == JsonValueKind.Object
            ? direct.Clone() : null;
    }

    private HttpRequestMessage CreateJsonRequest(string endpoint, object payload)
        => new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private async Task<JsonElement> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tavily {request.RequestUri} failed ({(int)response.StatusCode}): {body}",
                null, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string? GetString(JsonElement json, string name)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement? GetProperty(JsonElement json, string name)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static List<TavilySource> ParseSources(JsonElement sources)
        => sources.ValueKind != JsonValueKind.Array ? [] : sources.EnumerateArray()
            .Where(source => !string.IsNullOrWhiteSpace(GetString(source, "url")))
            .Select(source => new TavilySource
            {
                Url = GetString(source, "url")!, Title = GetString(source, "title"),
                Favicon = GetString(source, "favicon"), Raw = source.Clone()
            }).ToList();

    private static Dictionary<string, object?> ResponseMetadata(JsonElement root, string model)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["tavily.model"] = model,
            ["tavily.response.raw"] = root.Clone(),
            ["tavily"] = root.Clone()
        };
        foreach (var property in root.EnumerateObject())
            metadata[$"tavily.{property.Name}"] = property.Value.Clone();
        var id = GetString(root, "request_id") ?? $"tavily_{Guid.NewGuid():N}";
        var created = DateTimeOffset.TryParse(GetString(root, "created_at"), out var date)
            ? date.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        metadata["responses.id"] = metadata["chatcompletions.response.id"] = id;
        metadata["responses.created_at"] = metadata["chatcompletions.response.created"] = created;
        metadata["responses.completed_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return metadata;
    }

    private AIResponse CreateResponse(AIRequest request, string model, JsonElement root, string text, List<TavilySource> sources)
    {
        var metadata = ResponseMetadata(root, model);
        var items = new List<AIOutputItem>
        {
            new()
            {
                Type = "message", Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = text, Metadata = metadata }]
            }
        };
        items.AddRange(sources.DistinctBy(source => UrlKey(source.Url)).Select(CreateSourceOutputItem));
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model ?? $"tavily/{model}", Status = "completed",
            Usage = GetProperty(root, "usage"), Output = new AIOutput { Items = items }, Metadata = metadata
        };
    }

    private static AIOutputItem CreateSourceOutputItem(TavilySource source)
        => new()
        {
            Type = "source-url",
            Content = [new AITextContentPart { Type = "text", Text = source.Title ?? source.Url }],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = source.Url, ["chatcompletions.source.title"] = source.Title ?? source.Url,
                ["messages.source.url"] = source.Url, ["messages.source.title"] = source.Title ?? source.Url,
                ["tavily.source.raw"] = source.Raw, ["tavily.source.favicon"] = source.Favicon
            }
        };

    private static Dictionary<string, object> TextProviderMetadata(Dictionary<string, object?> metadata)
        => new() { ["tavily"] = metadata.Where(p => p.Key.StartsWith("tavily.", StringComparison.Ordinal))
            .ToDictionary(p => p.Key["tavily.".Length..], p => p.Value) };

    private static object ToSourceDto(TavilySource source)
        => source.Raw is JsonElement raw ? raw : new { url = source.Url, title = source.Title, favicon = source.Favicon };

    private sealed class TavilySource
    {
        public required string Url { get; init; }
        public string? Title { get; init; }
        public string? Favicon { get; init; }
        public JsonElement? Raw { get; init; }
    }
}
