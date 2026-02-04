using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Vidu;

/// <summary>
/// ProviderOptions schema for Vidu video generation.
/// Consumed via <c>providerOptions.vidu</c> for the unified video flow.
/// </summary>
public sealed class ViduVideoProviderMetadata
{
    /// <summary>
    /// Style for text-to-video. Defaults to general, supports anime.
    /// </summary>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Motion amplitude for Vidu models.
    /// </summary>
    [JsonPropertyName("movement_amplitude")]
    public string? MovementAmplitude { get; set; }

    /// <summary>
    /// Whether to generate audio along with the video.
    /// </summary>
    [JsonPropertyName("audio")]
    public bool? Audio { get; set; }

    /// <summary>
    /// Voice ID for audio-video generation (image-to-video only).
    /// </summary>
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    /// <summary>
    /// Whether to add background music.
    /// </summary>
    [JsonPropertyName("bgm")]
    public bool? Bgm { get; set; }

    /// <summary>
    /// Whether to use off-peak mode.
    /// </summary>
    [JsonPropertyName("off_peak")]
    public bool? OffPeak { get; set; }

    /// <summary>
    /// Transparent transmission payload.
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }
}
