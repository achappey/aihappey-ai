using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

/// <summary>
/// ProviderOptions payload for Freepik video generation.
/// </summary>
public sealed class FreepikVideoProviderMetadata
{
    /// <summary>
    /// LTX Video 2.0 Pro options.
    /// </summary>
    [JsonPropertyName("ltx")]
    public FreepikLtxVideoOptions? Ltx { get; set; }

    /// <summary>
    /// Kling 2.6 Pro options.
    /// </summary>
    [JsonPropertyName("kling")]
    public FreepikKlingVideoOptions? Kling { get; set; }
}

public sealed class FreepikLtxVideoOptions
{
    /// <summary>
    /// Whether to generate synchronized audio for the video.
    /// </summary>
    [JsonPropertyName("generate_audio")]
    public bool? GenerateAudio { get; set; }
}

public sealed class FreepikKlingVideoOptions
{
    /// <summary>
    /// Negative prompt to steer Kling away from undesired content.
    /// </summary>
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    /// <summary>
    /// CFG scale (0-1). Higher values increase adherence to the prompt.
    /// </summary>
    [JsonPropertyName("cfg_scale")]
    public double? CfgScale { get; set; }

    /// <summary>
    /// Whether to generate audio for the Kling video.
    /// </summary>
    [JsonPropertyName("generate_audio")]
    public bool? GenerateAudio { get; set; }
}
