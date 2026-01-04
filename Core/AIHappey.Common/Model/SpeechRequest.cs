using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model;

public class SpeechRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

}

public class SpeechResponse
{
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    public object Audio { get; set; } = null!;

    public IEnumerable<object> Warnings { get; set; } = [];

    public ResponseData Response { get; set; } = default!;

}
