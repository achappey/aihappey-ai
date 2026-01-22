using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class UpscalerCreative
{
    /// <summary>
    /// Configure scale factor of the image. Allowed values: 2x, 4x, 8x, 16x.
    /// </summary>
    [JsonPropertyName("scale_factor")]
    public string? ScaleFactor { get; set; }

    /// <summary>
    /// Styles to optimize the upscale process.
    /// Allowed values: standard, soft_portraits, hard_portraits, art_n_illustration, videogame_assets,
    /// nature_n_landscapes, films_n_photography, 3d_renders, science_fiction_n_horror.
    /// </summary>
    [JsonPropertyName("optimized_for")]
    public string? OptimizedFor { get; set; }

    /// <summary>
    /// Increase or decrease AI's creativity. Range: -10 to 10.
    /// </summary>
    [JsonPropertyName("creativity")]
    public int? Creativity { get; set; }

    /// <summary>
    /// Increase or decrease definition and detail. Range: -10 to 10.
    /// </summary>
    [JsonPropertyName("hdr")]
    public int? Hdr { get; set; }

    /// <summary>
    /// Adjust resemblance to the original image. Range: -10 to 10.
    /// </summary>
    [JsonPropertyName("resemblance")]
    public int? Resemblance { get; set; }

    /// <summary>
    /// Control prompt strength and intricacy. Range: -10 to 10.
    /// </summary>
    [JsonPropertyName("fractality")]
    public int? Fractality { get; set; }

    /// <summary>
    /// Magnific model engine. Allowed values: automatic, magnific_illusio, magnific_sharpy, magnific_sparkle.
    /// </summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }
}

