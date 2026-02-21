using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Infomaniak;

public sealed class InfomaniakImageProviderMetadata
{
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }
}

