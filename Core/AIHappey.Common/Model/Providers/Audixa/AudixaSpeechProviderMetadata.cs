using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Audixa;

/// <summary>
/// ProviderOptions schema for Audixa text-to-speech.
/// Maps to <c>POST https://api.audixa.ai/v2/tts</c> parameters.
/// </summary>
public sealed class AudixaSpeechProviderMetadata
{
    /// <summary>
    /// Voice ID to use for synthesis.
    /// </summary>
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    /// <summary>
    /// Playback speed multiplier (0.5..2.0).
    /// </summary>
    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    /// <summary>
    /// Emotional tone (advance model only). neutral|happy|sad|angry|surprised.
    /// </summary>
    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    /// <summary>
    /// Creativity control (advance model only). 0.7..1.0
    /// </summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// Plausibility filter (advance model only). 0.7..0.98
    /// </summary>
    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }
}
