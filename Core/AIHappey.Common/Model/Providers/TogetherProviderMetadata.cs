
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;


public class TogetherImageProviderMetadata
{
    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }

    [JsonPropertyName("disable_safety_checker")]
    public bool? DisableSafetyChecker { get; set; }

}

public class TogetherProviderMetadata
{

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}
