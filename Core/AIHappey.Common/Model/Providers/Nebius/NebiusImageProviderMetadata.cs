using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Nebius;

public sealed class NebiusImageProviderMetadata
{
    [JsonPropertyName("num_inference_steps")]
    public int? NumInferenceSteps { get; set; } //Required range: 1 <= x <= 80

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; } //Maximum string length: 2000

    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; } //Required range: 0 <= x <= 100

    [JsonPropertyName("response_extension")]
    public string? ResponseExtension { get; set; } // webp, jpg or png
}

