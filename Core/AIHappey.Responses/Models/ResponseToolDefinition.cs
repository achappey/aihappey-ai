
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// Minimal typed tool object: keep flexible until you want to type all variants.
/// Example: { "type": "web_search" } or { "type":"function", "name": ..., "parameters": ... }
/// </summary>
public sealed class ResponseToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class ProviderMetadataExtensions
{

    public static IReadOnlyDictionary<string, string>? GetResponseHeaders(
        this Dictionary<string, object?>? metadata,
        string providerId)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(providerId, out var providerObj) || providerObj is null)
            return null;

        JsonElement? headersEl = providerObj switch
        {
            // Already JsonElement
            JsonElement je when je.ValueKind == JsonValueKind.Object &&
                                je.TryGetProperty("headers", out var h) &&
                                h.ValueKind == JsonValueKind.Object
                => h,

            // JsonNode world
            System.Text.Json.Nodes.JsonObject jo
                when jo["headers"] is System.Text.Json.Nodes.JsonObject headersObj
                => ToJsonElement(headersObj),

            // Dictionary<string, object?>
            Dictionary<string, object?> dict
                when dict.TryGetValue("headers", out var headersObj)
                => ToJsonElement(headersObj),

            // Fallback: serialize anything
            _ => TryExtractHeaders(providerObj)
        };

        if (headersEl is null || headersEl.Value.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in headersEl.Value.EnumerateObject())
        {
            var name = prop.Name?.Trim();

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var value = ReadHeaderValue(prop.Value);

            if (string.IsNullOrWhiteSpace(value))
                continue;

            result[name] = value;
        }

        return result.Count > 0 ? result : null;
    }

    private static JsonElement? TryExtractHeaders(object obj)
    {
        try
        {
            var el = JsonSerializer.SerializeToElement(obj, JsonSerializerOptions.Web);

            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty("headers", out var headers) &&
                headers.ValueKind == JsonValueKind.Object)
            {
                return headers;
            }
        }
        catch
        {
            // passthrough safety
        }

        return null;
    }

    private static string? ReadHeaderValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",

            // Ignore null/object/array for headers.
            _ => null
        };
    }

    public static List<ResponseToolDefinition>? GetResponseToolDefinitions(
    this Dictionary<string, object?>? metadata,
    string providerId)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(providerId, out var providerObj) || providerObj is null)
            return null;

        JsonElement? toolsEl = providerObj switch
        {
            // ✅ Already JsonElement
            JsonElement je when je.ValueKind == JsonValueKind.Object &&
                                je.TryGetProperty("tools", out var t) &&
                                t.ValueKind == JsonValueKind.Array
                => t,

            // ✅ JsonNode world
            System.Text.Json.Nodes.JsonObject jo
                when jo["tools"] is System.Text.Json.Nodes.JsonArray arr
                => ToJsonElement(arr),

            // ✅ Dictionary<string, object?>
            Dictionary<string, object?> dict
                when dict.TryGetValue("tools", out var toolsObj)
                => ToJsonElement(toolsObj),

            // ✅ Fallback: try serialize anything
            _ => TryExtractTools(providerObj)
        };

        if (toolsEl is null)
            return null;

        var result = new List<ResponseToolDefinition>();

        foreach (var toolEl in toolsEl.Value.EnumerateArray())
        {
            try
            {
                var def = toolEl.Deserialize<ResponseToolDefinition>(JsonSerializerOptions.Web);
                if (def != null)
                    result.Add(def);
            }
            catch
            {
                // swallow — passthrough safety
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static JsonElement? TryExtractTools(object obj)
    {
        try
        {
            var el = JsonSerializer.SerializeToElement(obj, JsonSerializerOptions.Web);

            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty("tools", out var tools) &&
                tools.ValueKind == JsonValueKind.Array)
            {
                return tools;
            }
        }
        catch { }

        return null;
    }

    private static JsonElement ToJsonElement(object? obj)
    {
        if (obj is JsonElement je)
            return je;

        if (obj is System.Text.Json.Nodes.JsonNode node)
            return JsonSerializer.SerializeToElement(node, JsonSerializerOptions.Web);

        return JsonSerializer.SerializeToElement(obj, JsonSerializerOptions.Web);
    }

    public static List<ResponseToolDefinition>? GetResponseToolDefinitions2(
        this Dictionary<string, object?>? metadata,
        string providerId)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(providerId, out var providerObj))
            return null;

        if (providerObj is not JsonElement providerJson ||
            providerJson.ValueKind != JsonValueKind.Object)
            return null;

        if (!providerJson.TryGetProperty("tools", out var toolsEl) ||
            toolsEl.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<ResponseToolDefinition>();

        foreach (var toolEl in toolsEl.EnumerateArray())
        {
            try
            {
                var def = toolEl.Deserialize<ResponseToolDefinition>(JsonSerializerOptions.Web);
                if (def != null)
                    result.Add(def);
            }
            catch
            {
                // ignore invalid tool entries (passthrough safety)
            }
        }

        return result.Count > 0 ? result : null;
    }
}