using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class UpscalerPrecisionV2
{
    /// <summary>
    /// Sharpen the image. Range: 0-100.
    /// </summary>
    [JsonPropertyName("sharpen")]
    public int? Sharpen { get; set; }

    /// <summary>
    /// Smart grain. Range: 0-100.
    /// </summary>
    [JsonPropertyName("smart_grain")]
    public int? SmartGrain { get; set; }

    /// <summary>
    /// Ultra detail. Range: 0-100.
    /// </summary>
    [JsonPropertyName("ultra_detail")]
    public int? UltraDetail { get; set; }

    /// <summary>
    /// Upscaling style/flavor. Allowed values: sublime, photo, photo_denoiser.
    /// </summary>
    [JsonPropertyName("flavor")]
    public string? Flavor { get; set; }

    /// <summary>
    /// Image scaling factor. Range: 2-16.
    /// </summary>
    [JsonPropertyName("scale_factor")]
    public int? ScaleFactor { get; set; }
}

