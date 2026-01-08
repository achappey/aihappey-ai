using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiImageEditProviderMetadata
{
    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("input_fidelity")]
    public string? InputFidelity { get; set; }
}

