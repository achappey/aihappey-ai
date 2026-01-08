using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.StabilityAI;

public sealed class StabilityAISpeechProviderMetadata
{
    // Stable Audio (v2beta) text-to-audio
    // https://api.stability.ai/v2beta/audio/stable-audio-2/text-to-audio

    [JsonPropertyName("model")]
    public string? Model { get; set; }

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

