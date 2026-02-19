using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.APIpie;

public sealed class APIpieTranscriptionProviderMetadata
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("responseFormat")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("timestampGranularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}

