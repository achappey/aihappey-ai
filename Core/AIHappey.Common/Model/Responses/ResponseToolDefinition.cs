
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

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
