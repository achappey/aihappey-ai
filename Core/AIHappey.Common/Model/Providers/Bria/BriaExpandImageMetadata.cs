using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Bria;

public class BriaExpandImageMetadata
{
    [JsonPropertyName("canvas_size")]
    public int[]? CanvasSize { get; set; }

    [JsonPropertyName("original_image_size")]
    public int[]? OriginalImageSize { get; set; }

    [JsonPropertyName("original_image_location")]
    public int[]? OriginalImageLocation { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

}
