using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Bria;

public class BriaReplaceBackgroundMetadata
{
    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("refine_prompt")]
    public bool? RefinePrompt { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("ref_images")]
    public object? RefImages { get; set; }

    [JsonPropertyName("enhance_ref_images")]
    public bool? EnhanceRefImages { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("original_quality")]
    public bool? OriginalQuality { get; set; }

    [JsonPropertyName("force_background_detection")]
    public bool? ForceBackgroundDetection { get; set; }

}
