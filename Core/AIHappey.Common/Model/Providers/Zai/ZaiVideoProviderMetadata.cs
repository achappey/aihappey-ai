using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Zai;

/// <summary>
/// Provider-specific metadata for Z.AI video generation.
/// </summary>
public sealed class ZaiVideoProviderMetadata
{
    /// <summary>
    /// Output mode for CogVideoX.
    /// </summary>
    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    /// <summary>
    /// Whether to generate AI sound effects or background music (model-dependent).
    /// </summary>
    [JsonPropertyName("with_audio")]
    public bool? WithAudio { get; set; }

    /// <summary>
    /// Style for Vidu text-to-video.
    /// </summary>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Motion amplitude for Vidu models.
    /// </summary>
    [JsonPropertyName("movement_amplitude")]
    public string? MovementAmplitude { get; set; }

}
