using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.YourVoic;

/// <summary>
/// Provider-specific options for YourVoic Speech-to-Text batch transcription.
/// </summary>
public sealed class YourVoicTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    // Cipher options
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public string? TimestampGranularities { get; set; }

    // Lucid options
    [JsonPropertyName("diarize")]
    public bool? Diarize { get; set; }

    [JsonPropertyName("smart_format")]
    public bool? SmartFormat { get; set; }

    [JsonPropertyName("punctuate")]
    public bool? Punctuate { get; set; }

    [JsonPropertyName("keywords")]
    public string? Keywords { get; set; }
}

