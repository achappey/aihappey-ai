using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class ScalewayTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }
}
