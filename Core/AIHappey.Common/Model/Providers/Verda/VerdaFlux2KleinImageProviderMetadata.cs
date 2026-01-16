using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Verda;

public sealed class VerdaFlux2KleinImageProviderMetadata
{
    [JsonPropertyName("num_steps")]
    public int? NumSteps { get; set; }

    [JsonPropertyName("guidance")]
    public float? Guidance { get; set; }

    [JsonPropertyName("enable_safety_checker")]
    public bool? EnableSafetyChecker { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; } //"jpeg", "png", "webp"

    [JsonPropertyName("output_quality")]
    public int? OutputQuality { get; set; }
}
