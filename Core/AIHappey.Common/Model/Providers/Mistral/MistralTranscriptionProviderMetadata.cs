using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}

