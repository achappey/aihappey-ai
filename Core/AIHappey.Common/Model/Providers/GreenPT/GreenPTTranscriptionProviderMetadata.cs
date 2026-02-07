using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.GreenPT;

/// <summary>
/// Provider-specific options for GreenPT pre-recorded STT.
/// Values map to query parameters on <c>POST /v1/listen</c>.
/// </summary>
public sealed class GreenPTTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("diarize")]
    public bool? Diarize { get; set; }

    [JsonPropertyName("punctuate")]
    public bool? Punctuate { get; set; }

    [JsonPropertyName("smart_format")]
    public bool? SmartFormat { get; set; }

    [JsonPropertyName("filler_words")]
    public bool? FillerWords { get; set; }

    [JsonPropertyName("numerals")]
    public bool? Numerals { get; set; }

    [JsonPropertyName("sentiment")]
    public bool? Sentiment { get; set; }

    [JsonPropertyName("topics")]
    public bool? Topics { get; set; }

    [JsonPropertyName("intents")]
    public bool? Intents { get; set; }
}

