using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.MurfAI;

/// <summary>
/// Provider-specific options for MurfAI Text-to-Speech.
/// Maps to <c>POST https://api.murf.ai/v1/speech/generate</c>.
/// </summary>
public sealed class MurfAISpeechProviderMetadata
{
    /// <summary>
    /// Murf voice id or voice actor name.
    /// Required by Murf.
    /// </summary>
    [JsonPropertyName("voiceId")]
    public string? VoiceId { get; set; }

    /// <summary>
    /// Duration (seconds) for generated audio. If 0, Murf ignores it.
    /// Gen2 only.
    /// </summary>
    [JsonPropertyName("audioDuration")]
    public double? AudioDuration { get; set; }

    /// <summary>
    /// Channel type. Valid values: STEREO, MONO.
    /// </summary>
    [JsonPropertyName("channelType")]
    public string? ChannelType { get; set; }

    /// <summary>
    /// When true, Murf returns <c>encodedAudio</c> in the JSON response.
    /// If false, Murf returns <c>audioFile</c> URL.
    /// </summary>
    [JsonPropertyName("encodeAsBase64")]
    public bool? EncodeAsBase64 { get; set; }

    /// <summary>
    /// Output file format. Valid values: MP3, WAV, FLAC, ALAW, ULAW, PCM, OGG.
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>
    /// Model version. Murf currently documents GEN2.
    /// </summary>
    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; set; }

    /// <summary>
    /// Multi-native locale (IETF language tag like en-US).
    /// </summary>
    [JsonPropertyName("multiNativeLocale")]
    public string? MultiNativeLocale { get; set; }

    /// <summary>
    /// Pitch of voiceover. Range -50..50.
    /// </summary>
    [JsonPropertyName("pitch")]
    public int? Pitch { get; set; }

    /// <summary>
    /// Speed of voiceover. Range -50..50.
    /// </summary>
    [JsonPropertyName("rate")]
    public int? Rate { get; set; }

    /// <summary>
    /// Audio sample rate in Hz. Valid values include 8000, 24000, 44100, 48000.
    /// </summary>
    [JsonPropertyName("sampleRate")]
    public double? SampleRate { get; set; }

    /// <summary>
    /// Optional voice style.
    /// </summary>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Variation parameter. Range 0..5.
    /// Gen2 only.
    /// </summary>
    [JsonPropertyName("variation")]
    public int? Variation { get; set; }

    /// <summary>
    /// When true, word durations return words as the original input text. English only.
    /// </summary>
    [JsonPropertyName("wordDurationsAsOriginalText")]
    public bool? WordDurationsAsOriginalText { get; set; }

    /// <summary>
    /// Pronunciation dictionary entries.
    /// Example: {"2022":{"type":"SAY_AS","pronunciation":"twenty twenty two"}}
    /// </summary>
    [JsonPropertyName("pronunciationDictionary")]
    public Dictionary<string, MurfPronunciationEntry>? PronunciationDictionary { get; set; }
}

public sealed class MurfPronunciationEntry
{
    [JsonPropertyName("pronunciation")]
    public string? Pronunciation { get; set; }

    /// <summary>
    /// Allowed values: IPA, SAY_AS.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

