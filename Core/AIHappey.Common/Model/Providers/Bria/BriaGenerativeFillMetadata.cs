using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Bria;

public class BriaGenerativeFillMetadata
{
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("refine_prompt")]
    public bool? RefinePrompt { get; set; }

    [JsonPropertyName("tailored_model_id")]
    public string? TailoredModelId { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("mask_type")]
    public string? MaskType { get; set; }
}
