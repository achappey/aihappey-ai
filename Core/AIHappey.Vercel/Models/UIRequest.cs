using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

public class UIRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = null!;

    [JsonPropertyName("catalogPrompt")]
    public string CatalogPrompt { get; set; } = null!;

    [JsonPropertyName("context")]
    public object? Context { get; set; }

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 1;

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}
