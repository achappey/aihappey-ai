using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

/// <summary>
/// Freepik providerOptions payload for image/icon generation.
/// </summary>
public sealed class FreepikImageProviderMetadata
{
    /// <summary>
    /// Icon generation settings for Freepik Icon Generation API.
    /// </summary>
    [JsonPropertyName("icon_generation")]
    public IconGeneration? IconGeneration { get; set; }

    /// <summary>
    /// Skin enhancer settings for Freepik Skin Enhancer API.
    /// </summary>
    [JsonPropertyName("skin_enhancer")]
    public SkinEnhancer? SkinEnhancer { get; set; }

    /// <summary>
    /// Image expand settings for Freepik Image Expand API.
    /// </summary>
    [JsonPropertyName("image_expand")]
    public ImageExpand? ImageExpand { get; set; }

    /// <summary>
    /// Upscaler settings for Freepik Upscaler (Magnific) APIs.
    /// </summary>
    [JsonPropertyName("upscaler")]
    public Upscaler? Upscaler { get; set; }

    /// <summary>
    /// Relight settings for Freepik Relight (Magnific) API.
    /// </summary>
    /// <remarks>
    /// Maps 1:1 to POST /v1/ai/image-relight body, excluding <c>image</c>, <c>prompt</c>, and <c>webhook_url</c>.
    /// Transfer sources are mutually exclusive: provide either <c>transfer_light_from_reference_image</c>
    /// or <c>transfer_light_from_lightmap</c>.
    /// </remarks>
    [JsonPropertyName("relight")]
    public Relight? Relight { get; set; }

    /// <summary>
    /// (Beta) Reimagine Flux settings.
    /// </summary>
    /// <remarks>
    /// Maps 1:1 to POST /v1/ai/beta/text-to-image/reimagine-flux body, excluding
    /// <c>image</c>, <c>prompt</c>, and <c>webhook_url</c>.
    /// Prompt must come from <see cref="AIHappey.Common.Model.ImageRequest.Prompt"/>.
    /// </remarks>
    [JsonPropertyName("reimagine_flux")]
    public ReimagineFlux? ReimagineFlux { get; set; }

}

public sealed class ReimagineFlux
{
    /// <summary>
    /// Imagination type.
    /// </summary>
    /// <remarks>Allowed values: wild, subtle, vivid.</remarks>
    [JsonPropertyName("imagination")]
    public string? Imagination { get; set; }

    /// <summary>
    /// Output aspect ratio.
    /// </summary>
    /// <remarks>
    /// Allowed values: original, square_1_1, classic_4_3, traditional_3_4, widescreen_16_9,
    /// social_story_9_16, standard_3_2, portrait_2_3, horizontal_2_1, vertical_1_2, social_post_4_5.
    /// </remarks>
    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }
}

public sealed class Relight
{
    /// <summary>
    /// Base64 of the reference image used for light transfer.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="TransferLightFromLightmap"/>.
    /// Base64-only (no URLs/data URLs).
    /// </remarks>
 //   [JsonPropertyName("transfer_light_from_reference_image")]
 //   public string? TransferLightFromReferenceImage { get; set; }

    /// <summary>
    /// Base64 of the lightmap used for light transfer.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="TransferLightFromReferenceImage"/>.
    /// Base64-only (no URLs/data URLs).
    /// </remarks>
   // [JsonPropertyName("transfer_light_from_lightmap")]
   // public string? TransferLightFromLightmap { get; set; }

    /// <summary>
    /// Light transfer strength (0-100).
    /// </summary>
    [JsonPropertyName("light_transfer_strength")]
    public int? LightTransferStrength { get; set; }

    /// <summary>
    /// Interpolate from original.
    /// </summary>
    [JsonPropertyName("interpolate_from_original")]
    public bool? InterpolateFromOriginal { get; set; }

    /// <summary>
    /// Change background.
    /// </summary>
    [JsonPropertyName("change_background")]
    public bool? ChangeBackground { get; set; }

    /// <summary>
    /// Relight style.
    /// </summary>
    /// <remarks>
    /// Allowed values (per docs): standard, darker_but_realistic, clean, smooth, brighter,
    /// contrasted_n_hdr, just_composition.
    /// </remarks>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Preserve details.
    /// </summary>
    [JsonPropertyName("preserve_details")]
    public bool? PreserveDetails { get; set; }

    /// <summary>
    /// Advanced settings.
    /// </summary>
    [JsonPropertyName("advanced_settings")]
    public RelightAdvancedSettings? AdvancedSettings { get; set; }
}

public sealed class RelightAdvancedSettings
{
    [JsonPropertyName("whites")]
    public int? Whites { get; set; }

    [JsonPropertyName("blacks")]
    public int? Blacks { get; set; }

    [JsonPropertyName("brightness")]
    public int? Brightness { get; set; }

    [JsonPropertyName("contrast")]
    public int? Contrast { get; set; }

    [JsonPropertyName("saturation")]
    public int? Saturation { get; set; }

    /// <summary>
    /// Relight engine.
    /// </summary>
    /// <remarks>Docs example uses "illusio".</remarks>
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    /// <summary>
    /// Transfer light A.
    /// </summary>
    [JsonPropertyName("transfer_light_a")]
    public string? TransferLightA { get; set; }

    /// <summary>
    /// Transfer light B.
    /// </summary>
    [JsonPropertyName("transfer_light_b")]
    public string? TransferLightB { get; set; }

    [JsonPropertyName("fixed_generation")]
    public bool? FixedGeneration { get; set; }
}

public sealed class Upscaler
{
    /// <summary>
    /// Upscaler Creative settings (POST /v1/ai/image-upscaler).
    /// </summary>
    [JsonPropertyName("creative")]
    public UpscalerCreative? Creative { get; set; }

    /// <summary>
    /// Upscaler Precision V1 settings (POST /v1/ai/image-upscaler-precision).
    /// </summary>
    [JsonPropertyName("precision")]
    public UpscalerPrecisionV1? Precision { get; set; }

    /// <summary>
    /// Upscaler Precision V2 settings (POST /v1/ai/image-upscaler-precision-v2).
    /// </summary>
    [JsonPropertyName("precision_v2")]
    public UpscalerPrecisionV2? PrecisionV2 { get; set; }
}

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

public sealed class UpscalerPrecisionV1
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
}

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

public sealed class ImageExpand
{
    /// <summary>
    /// Pixels to expand on the left.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("left")]
    public int? Left { get; set; }

    /// <summary>
    /// Pixels to expand on the right.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("right")]
    public int? Right { get; set; }

    /// <summary>
    /// Pixels to expand on the top.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("top")]
    public int? Top { get; set; }

    /// <summary>
    /// Pixels to expand on the bottom.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("bottom")]
    public int? Bottom { get; set; }
}

public sealed class SkinEnhancer
{
    /// <summary>
    /// Creative mode settings.
    /// </summary>
    [JsonPropertyName("creative")]
    public SkinEnhancerCreative? Creative { get; set; }

    /// <summary>
    /// Faithful mode settings.
    /// </summary>
    [JsonPropertyName("faithful")]
    public SkinEnhancerFaithful? Faithful { get; set; }

    /// <summary>
    /// Flexible mode settings.
    /// </summary>
    [JsonPropertyName("flexible")]
    public SkinEnhancerFlexible? Flexible { get; set; }
}

public class SkinEnhancerBase
{
    /// <summary>
    /// Sharpening intensity.
    /// </summary>
    /// <remarks>Range: 0-100.</remarks>
    [JsonPropertyName("sharpen")]
    public int? Sharpen { get; set; }

    /// <summary>
    /// Smart grain intensity.
    /// </summary>
    /// <remarks>Range: 0-100.</remarks>
    [JsonPropertyName("smart_grain")]
    public int? SmartGrain { get; set; }
}

public sealed class SkinEnhancerCreative : SkinEnhancerBase
{
}

public sealed class SkinEnhancerFaithful : SkinEnhancerBase
{
    /// <summary>
    /// Skin detail enhancement level.
    /// </summary>
    /// <remarks>Range: 0-100.</remarks>
    [JsonPropertyName("skin_detail")]
    public int? SkinDetail { get; set; }
}

public sealed class SkinEnhancerFlexible : SkinEnhancerBase
{
    /// <summary>
    /// Optimization target for flexible skin enhancer.
    /// </summary>
    /// <remarks>Allowed values: enhance_skin, improve_lighting, enhance_everything, transform_to_real, no_make_up.</remarks>
    [JsonPropertyName("optimized_for")]
    public string? OptimizedFor { get; set; }
}

public sealed class IconGeneration
{
    /// <summary>
    /// Icon style.
    /// </summary>
    /// <remarks>Allowed values: solid, outline, color, flat, sticker.</remarks>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Number of inference steps.
    /// </summary>
    /// <remarks>Range: 10-50.</remarks>
    [JsonPropertyName("num_inference_steps")]
    public int? NumInferenceSteps { get; set; }

    /// <summary>
    /// Guidance scale.
    /// </summary>
    /// <remarks>Range: 0-10.</remarks>
    [JsonPropertyName("guidance_scale")]
    public double? GuidanceScale { get; set; }

    /// <summary>
    /// Output format for icon generation.
    /// </summary>
    /// <remarks>Allowed values: png, svg. Only used for the non-preview endpoint.</remarks>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

}
