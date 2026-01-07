
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class StabilityAIImageProviderMetadata
{

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("style_preset")]
    public string? StylePreset { get; set; }
}

public class StabilityAISpeechProviderMetadata
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
