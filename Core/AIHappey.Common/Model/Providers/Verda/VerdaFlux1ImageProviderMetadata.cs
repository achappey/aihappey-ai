using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Verda;

public sealed class VerdaFlux1ImageProviderMetadata
{
    [JsonPropertyName("num_inference_steps")]
    public int? NumInferenceSteps { get; set; }

    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }

    [JsonPropertyName("enable_safety_checker")]
    public bool? EnableSafetyChecker { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; } //"jpeg", "png", "webp"

    [JsonPropertyName("output_quality")]
    public int? OutputQuality { get; set; }
}
