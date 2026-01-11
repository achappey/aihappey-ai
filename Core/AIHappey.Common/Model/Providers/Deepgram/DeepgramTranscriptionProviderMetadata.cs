using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Deepgram;

/// <summary>
/// Provider-specific options for Deepgram Speech-to-Text (POST /v1/listen).
/// Values map to Deepgram query parameters.
/// </summary>
public sealed class DeepgramTranscriptionProviderMetadata
{
    // ---- Common knobs ----
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("punctuate")]
    public bool? Punctuate { get; set; }

    [JsonPropertyName("smart_format")]
    public bool? SmartFormat { get; set; }

    [JsonPropertyName("paragraphs")]
    public bool? Paragraphs { get; set; }

    [JsonPropertyName("utterances")]
    public bool? Utterances { get; set; }

    [JsonPropertyName("diarize")]
    public bool? Diarize { get; set; }

    [JsonPropertyName("multichannel")]
    public bool? Multichannel { get; set; }

    [JsonPropertyName("detect_language")]
    public JsonElement? DetectLanguage { get; set; } // boolean OR list-of-strings

    [JsonPropertyName("detect_entities")]
    public bool? DetectEntities { get; set; }

    [JsonPropertyName("topics")]
    public bool? Topics { get; set; }

    [JsonPropertyName("intents")]
    public bool? Intents { get; set; }

    [JsonPropertyName("sentiment")]
    public bool? Sentiment { get; set; }

    [JsonPropertyName("mip_opt_out")]
    public bool? MipOptOut { get; set; }

    // Usage reporting tag (Deepgram accepts string or list-of-strings)
    [JsonPropertyName("tag")]
    public JsonElement? Tag { get; set; }
}

