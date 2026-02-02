using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Together;

public sealed class TogetherVideoProviderMetadata
{
    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("output_quality")]
    public int? OutputQuality { get; set; }
}
