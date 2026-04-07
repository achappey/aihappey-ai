
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