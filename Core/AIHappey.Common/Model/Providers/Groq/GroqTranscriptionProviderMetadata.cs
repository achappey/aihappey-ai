using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Groq;

public sealed class GroqTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}

