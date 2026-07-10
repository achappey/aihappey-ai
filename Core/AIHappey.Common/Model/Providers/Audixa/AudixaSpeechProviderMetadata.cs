using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Audixa;

/// <summary>
/// ProviderOptions schema for Audixa text-to-speech.
/// Maps to <c>POST https://api.audixa.ai/v3/tts</c> parameters.
/// </summary>
public sealed class AudixaSpeechProviderMetadata
{
    /// <summary>
    /// Voice ID to use for synthesis. Get available voices from /voices.
    /// </summary>
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    /// <summary>
    /// Playback speed multiplier. Range: 0.5..2.0.
    /// </summary>
    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    /// <summary>
    /// Controls how strictly the advanced model follows the text/style. Range: 1.0..5.0.
    /// </summary>
    [JsonPropertyName("cfg_weight")]
    public float? CfgWeight { get; set; }

    /// <summary>
    /// Controls advanced-model emotional fluctuation/expressiveness. Range: 0.0..1.0.
    /// </summary>
    [JsonPropertyName("exaggeration")]
    public float? Exaggeration { get; set; }

    /// <summary>
    /// Output format: wav or mp3.
    /// </summary>
    [JsonPropertyName("audio_format")]
    public string? AudioFormat { get; set; }

    /// <summary>
    /// Optional Audixa language code. Base model uses single-letter codes; advanced model uses ISO 639-1 codes.
    /// </summary>
    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }
}
