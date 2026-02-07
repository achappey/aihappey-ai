using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Bria;

public class BriaGenerateImageMetadata
{
    [JsonPropertyName("structured_prompt")]
    public string? StructuredPrompt { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("guidance_scale")]
    public int? GuidanceScale { get; set; }

    [JsonPropertyName("steps_num")]
    public int? StepsNum { get; set; }

    [JsonPropertyName("ip_signal")]
    public bool? IpSignal { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

}
