using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Supertone;

/// <summary>
/// Provider options for Supertone TTS.
/// Consumed via <c>providerOptions.supertone</c>.
/// </summary>
public sealed class SupertoneSpeechProviderMetadata
{
    /// <summary>
    /// Optional language code (e.g. en).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Optional voice style. If omitted, provider default style is used.
    /// </summary>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Return phoneme timing data with the audio.
    /// </summary>
    [JsonPropertyName("includePhonemes")]
    public bool? IncludePhonemes { get; set; }

    /// <summary>
    /// Optional pre-normalized text (primarily useful for Japanese).
    /// </summary>
    [JsonPropertyName("normalizedText")]
    public string? NormalizedText { get; set; }

    /// <summary>
    /// Optional advanced voice settings.
    /// </summary>
    [JsonPropertyName("voiceSettings")]
    public SupertoneVoiceSettingsMetadata? VoiceSettings { get; set; }
}

public sealed class SupertoneVoiceSettingsMetadata
{
    [JsonPropertyName("pitchShift")]
    public float? PitchShift { get; set; }

    [JsonPropertyName("pitchVariance")]
    public float? PitchVariance { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("duration")]
    public float? Duration { get; set; }

    [JsonPropertyName("similarity")]
    public float? Similarity { get; set; }

    [JsonPropertyName("textGuidance")]
    public float? TextGuidance { get; set; }

    [JsonPropertyName("subharmonicAmplitudeControl")]
    public float? SubharmonicAmplitudeControl { get; set; }
}

