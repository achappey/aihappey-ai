using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Novita;

public class NovitaImageProviderMetadata
{
    [JsonPropertyName("seedream45")]
    public NovitaSeedream45ImageProviderMetadata? Seedream45 { get; set; }

    [JsonPropertyName("editor")]
    public NovitaImageEditorProviderMetadata? Editor { get; set; }

}

public sealed class NovitaSeedream45ImageProviderMetadata
{
    /// <summary>
    /// Whether to add a watermark to the generated images.
    /// Default: true
    /// </summary>
    [JsonPropertyName("watermark")]
    public bool? Watermark { get; set; } = true;

    /// <summary>
    /// Configuration for the prompt optimization feature.
    /// </summary>
    [JsonPropertyName("optimize_prompt_options")]
    public NovitaOptimizePromptOptions? OptimizePromptOptions { get; set; }

    /// <summary>
    /// Controls sequential image generation.
    /// Default: disabled
    /// </summary>
    [JsonPropertyName("sequential_image_generation")]
    public string? SequentialImageGeneration { get; set; }

    /// <summary>
    /// Configuration for sequential image generation.
    /// Only effective when sequential_image_generation = auto.
    /// </summary>
    [JsonPropertyName("sequential_image_generation_options")]
    public NovitaSequentialImageGenerationOptions? SequentialImageGenerationOptions { get; set; }
}

public sealed class NovitaOptimizePromptOptions
{
    /// <summary>
    /// Prompt optimization mode.
    /// Default: standard
    /// </summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

public sealed class NovitaSequentialImageGenerationOptions
{
    /// <summary>
    /// Maximum number of images that can be generated.
    /// Range: 1–15
    /// Default: 15
    /// </summary>
    [JsonPropertyName("max_images")]
    public int? MaxImages { get; set; } = 15;
}

public sealed class NovitaImageEditorProviderMetadata
{
    /// <summary>
    /// The returned image type.
    /// Enum: png, webp, jpeg
    /// </summary>
    [JsonPropertyName("extra")]
    public NovitaImageEditorExtra? Extra { get; set; }

}

public sealed class NovitaImageEditorExtra
{
    /// <summary>
    /// The returned image type.
    /// Enum: png, webp, jpeg
    /// </summary>
    [JsonPropertyName("response_image_type")]
    public string? ResponseImageType { get; set; }

}
