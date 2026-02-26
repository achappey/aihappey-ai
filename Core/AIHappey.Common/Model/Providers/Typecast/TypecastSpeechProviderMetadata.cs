using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Typecast;

/// <summary>
/// Provider options for Typecast TTS.
/// Consumed via <c>providerOptions.typecast</c>.
/// </summary>
public sealed class TypecastSpeechProviderMetadata
{
    /// <summary>
    /// Optional ISO 639-3 language code (e.g. eng).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Prompt emotion type. Examples: smart, preset.
    /// </summary>
    [JsonPropertyName("emotionType")]
    public string? EmotionType { get; set; }

    /// <summary>
    /// Optional preset emotion value (e.g. normal, happy, sad, angry, whisper).
    /// Applied as <c>prompt.emotion</c> when provided.
    /// </summary>
    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    /// <summary>
    /// Optional context text before current text for smart emotion.
    /// </summary>
    [JsonPropertyName("previousText")]
    public string? PreviousText { get; set; }

    /// <summary>
    /// Optional context text after current text for smart emotion.
    /// </summary>
    [JsonPropertyName("nextText")]
    public string? NextText { get; set; }

    /// <summary>
    /// Output volume in range 0..200.
    /// </summary>
    [JsonPropertyName("volume")]
    public int? Volume { get; set; }

    /// <summary>
    /// Output pitch in semitones in range -12..12.
    /// </summary>
    [JsonPropertyName("audioPitch")]
    public int? AudioPitch { get; set; }

    /// <summary>
    /// Output tempo multiplier in range 0.5..2.0.
    /// </summary>
    [JsonPropertyName("audioTempo")]
    public float? AudioTempo { get; set; }

    /// <summary>
    /// Optional random seed.
    /// </summary>
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }
}

