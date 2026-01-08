using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Hyperbolic;

public class HyperbolicImageProviderMetadata
{
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }
}

