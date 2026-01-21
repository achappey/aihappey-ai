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

    /// <summary>
    /// ElevenLabs language code (ISO 639-1) for TTS and Text-to-Dialogue.
    /// If not specified, the model auto-detects (provider default).
    /// </summary>
    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    /// <summary>
    /// Pronunciation dictionary locators (id + optional version) to apply.
    /// Applies to Text-to-Dialogue and some TTS endpoints.
    /// </summary>
    [JsonPropertyName("pronunciation_dictionary_locators")]
    public IEnumerable<ElevenLabsPronunciationDictionaryLocator>? PronunciationDictionaryLocators { get; set; }

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

    [JsonPropertyName("dialogue")]
    public ElevenLabsSpeechDialogue? Dialogue { get; set; }

    // ---- ElevenLabs Sound Generation (POST /v1/sound-generation) ----

    /// <summary>
    /// Text-to-sound generation settings for the Sound Effects endpoint.
    /// </summary>
    [JsonPropertyName("textToSound")]
    public ElevenLabsTextToSoundConfig? TextToSound { get; set; }
}

public sealed class ElevenLabsTextToSoundConfig
{
    /// <summary>
    /// Whether to create a sound effect that loops smoothly.
    /// Only available for the <c>eleven_text_to_sound_v2</c> model.
    /// </summary>
    [JsonPropertyName("loop")]
    public bool? Loop { get; set; }

    /// <summary>
    /// Duration of the generated sound in seconds. Range per ElevenLabs: 0.5..30.
    /// If null, ElevenLabs will choose an optimal duration.
    /// </summary>
    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// How strongly the generation follows the prompt. Range 0..1.
    /// </summary>
    [JsonPropertyName("prompt_influence")]
    public double? PromptInfluence { get; set; }
}

public sealed class ElevenLabsPronunciationDictionaryLocator
{
    [JsonPropertyName("pronunciation_dictionary_id")]
    public string PronunciationDictionaryId { get; set; } = null!;

    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }
}


public sealed class ElevenLabsSpeechDialogueInput
{
    [JsonPropertyName("voice_id")]
    public string VoiceId { get; set; } = null!;

    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

}

public sealed class ElevenLabsSpeechDialogue
{
    [JsonPropertyName("inputs")]
    public IEnumerable<ElevenLabsSpeechDialogueInput>? Inputs { get; set; }

    /// <summary>
    /// Text-to-Dialogue generation settings.
    /// </summary>
    [JsonPropertyName("settings")]
    public ElevenLabsSpeechDialogueSettings? Settings { get; set; }

}

public sealed class ElevenLabsSpeechDialogueSettings
{
    /// <summary>
    /// Determines how stable the voice is and the randomness between each generation.
    /// Range 0..1. Lower values introduce broader emotional range.
    /// </summary>
    [JsonPropertyName("stability")]
    public double? Stability { get; set; }
}
