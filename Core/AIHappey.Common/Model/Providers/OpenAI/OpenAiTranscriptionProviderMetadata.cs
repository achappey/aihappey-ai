using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("keywords")]
    public IEnumerable<string>? Keywords { get; set; }

    [JsonPropertyName("languages")]
    public IEnumerable<string>? Languages { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; } = "json";

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }

    [JsonPropertyName("known_speaker_references")]
    public IEnumerable<string>? KnownSpeakerReferences { get; set; }

    [JsonPropertyName("known_speaker_names")]
    public IEnumerable<string>? KnownSpeakerNames { get; set; }
}

