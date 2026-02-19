using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.MegaNova;

public sealed class MegaNovaImageProviderMetadata
{
    [JsonPropertyName("num_steps")]
    public int? NumSteps { get; set; }

    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }
}

