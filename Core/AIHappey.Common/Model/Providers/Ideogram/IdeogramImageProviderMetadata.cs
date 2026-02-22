using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Ideogram;

/// <summary>
/// Provider-specific metadata for Ideogram image endpoints.
/// </summary>
public sealed class IdeogramImageProviderMetadata
{
    [JsonPropertyName("rendering_speed")]
    public string? RenderingSpeed { get; set; }

    [JsonPropertyName("magic_prompt")]
    public string? MagicPrompt { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("num_images")]
    public int? NumImages { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("color_palette")]
    public JsonElement? ColorPalette { get; set; }

    [JsonPropertyName("style_codes")]
    public IReadOnlyList<string>? StyleCodes { get; set; }

    [JsonPropertyName("style_type")]
    public string? StyleType { get; set; }

    [JsonPropertyName("style_preset")]
    public string? StylePreset { get; set; }

    [JsonPropertyName("style_reference_images")]
    public IReadOnlyList<string>? StyleReferenceImages { get; set; }

    [JsonPropertyName("character_reference_images")]
    public IReadOnlyList<string>? CharacterReferenceImages { get; set; }

    [JsonPropertyName("character_reference_images_mask")]
    public IReadOnlyList<string>? CharacterReferenceImagesMask { get; set; }

    [JsonPropertyName("upscale_factor")]
    public string? UpscaleFactor { get; set; }

    [JsonPropertyName("image_weight")]
    public int? ImageWeight { get; set; }

    [JsonPropertyName("upscale")]
    public IdeogramUpscaleOptions? Upscale { get; set; }
}

public sealed class IdeogramUpscaleOptions
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("resemblance")]
    public int? Resemblance { get; set; }

    [JsonPropertyName("detail")]
    public int? Detail { get; set; }

    [JsonPropertyName("magic_prompt_option")]
    public string? MagicPromptOption { get; set; }

    [JsonPropertyName("num_images")]
    public int? NumImages { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }
}
