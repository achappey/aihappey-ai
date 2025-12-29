using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class AIMLImageProviderMetadata
{
    [JsonPropertyName("hunyuan")]
    public AIMLHunyuanImageSettings? Hunyuan { get; set; }

    [JsonPropertyName("openai")]
    public OpenAiImageProviderMetadata? OpenAI { get; set; }

}

public class AIMLHunyuanImageSettings
{
    [JsonPropertyName("enable_prompt_expansion")]
    public bool? EnablePromptExpansion { get; set; }

    [JsonPropertyName("enable_safety_checker")]
    public bool? EnableSafetyChecker { get; set; }

    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }

    [JsonPropertyName("num_inference_steps")]
    public int? NumInferenceSteps { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }
}

