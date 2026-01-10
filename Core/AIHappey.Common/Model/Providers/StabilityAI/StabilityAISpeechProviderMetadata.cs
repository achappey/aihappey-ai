using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.StabilityAI;

public sealed class StabilityAISpeechProviderMetadata
{
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("duration")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("seed")]
    public uint? Seed { get; set; }

    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("cfg_scale")]
    public double? CfgScale { get; set; }
}

