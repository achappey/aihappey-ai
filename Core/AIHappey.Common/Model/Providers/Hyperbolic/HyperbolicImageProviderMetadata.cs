using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Hyperbolic;

public class HyperbolicImageProviderMetadata
{
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("cfg_scale")]
    public float? CfgScale { get; set; }
}

