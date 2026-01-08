using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ElevenLabs;

public sealed class ElevenLabsSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    /// <summary>
    /// Query-string <c>output_format</c> e.g. <c>mp3_44100_128</c>.
    /// </summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("enable_logging")]
    public bool? EnableLogging { get; set; }

    [JsonPropertyName("seed")]
    public uint? Seed { get; set; }

    [JsonPropertyName("voice_settings")]
    public ElevenLabsVoiceSettings? VoiceSettings { get; set; }

    [JsonPropertyName("previous_text")]
    public string? PreviousText { get; set; }

    [JsonPropertyName("next_text")]
    public string? NextText { get; set; }

    [JsonPropertyName("apply_text_normalization")]
    public string? ApplyTextNormalization { get; set; } // auto | on | off

    [JsonPropertyName("apply_language_text_normalization")]
    public bool? ApplyLanguageTextNormalization { get; set; } 

    // ---- ElevenLabs Music (POST /v1/music) ----

    /// <summary>
    /// Optional length of generated music in milliseconds. Valid range per ElevenLabs: 3000..600000.
    /// </summary>
    [JsonPropertyName("music_length_ms")]
    public int? MusicLengthMs { get; set; }

    /// <summary>
    /// If true, guarantees instrumental output. Only applicable when using <c>prompt</c>.
    /// </summary>
    [JsonPropertyName("force_instrumental")]
    public bool? ForceInstrumental { get; set; }

    /// <summary>
    /// Controls how strictly composition plan section durations are respected.
    /// </summary>
    [JsonPropertyName("respect_sections_durations")]
    public bool? RespectSectionsDurations { get; set; }

    /// <summary>
    /// Whether to store the generated song for inpainting (enterprise-only).
    /// </summary>
    [JsonPropertyName("store_for_inpainting")]
    public bool? StoreForInpainting { get; set; }

    /// <summary>
    /// Whether to sign output with C2PA (applicable only for mp3).
    /// </summary>
    [JsonPropertyName("sign_with_c2pa")]
    public bool? SignWithC2pa { get; set; }
}

