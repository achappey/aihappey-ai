using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

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

    /// <summary>
    /// Text-to-image generation settings for Freepik Image Generation API.
    /// </summary>
    /// <remarks>
    /// Maps to the various <c>/v1/ai/text-to-image/*</c> endpoints.
    /// </remarks>
    [JsonPropertyName("image_generation")]
    public ImageGeneration.ImageGeneration? ImageGeneration { get; set; }
}
