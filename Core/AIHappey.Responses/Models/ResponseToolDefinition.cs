
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