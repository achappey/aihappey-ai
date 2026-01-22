using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

/// <summary>
/// ProviderOptions payload for Freepik "Classic Fast" image generation.
/// </summary>
/// <remarks>
/// Maps 1:1 to <c>POST /v1/ai/text-to-image</c> request body, excluding the required <c>prompt</c>
/// which is taken from <see cref="AIHappey.Common.Model.ImageRequest.Prompt"/>.
/// </remarks>
public sealed class ClassicFast
{
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }

    [JsonPropertyName("image")]
    public ClassicFastImage? Image { get; set; }

    [JsonPropertyName("styling")]
    public ClassicFastStyling? Styling { get; set; }

    [JsonPropertyName("filter_nsfw")]
    public bool? FilterNsfw { get; set; }
}

public sealed class ClassicFastImage
{
    /// <summary>
    /// Image size with the aspect ratio enum name (e.g. square_1_1, widescreen_16_9).
    /// </summary>
    [JsonPropertyName("size")]
    public string? Size { get; set; }
}

public sealed class ClassicFastStyling
{
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("effects")]
    public ClassicFastEffects? Effects { get; set; }

    /// <summary>
    /// Dominant colors to generate the image.
    /// </summary>
    [JsonPropertyName("colors")]
    public List<ClassicFastColor>? Colors { get; set; }
}

public sealed class ClassicFastEffects
{
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("lightning")]
    public string? Lightning { get; set; }

    [JsonPropertyName("framing")]
    public string? Framing { get; set; }
}

public sealed class ClassicFastColor
{
    [JsonPropertyName("color")]
    public string Color { get; set; } = null!;

    [JsonPropertyName("weight")]
    public float Weight { get; set; }
}

