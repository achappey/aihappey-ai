using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.StabilityAI;

public sealed class StabilityAIImageProviderMetadata
{
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("style_preset")]
    public string? StylePreset { get; set; }
}

