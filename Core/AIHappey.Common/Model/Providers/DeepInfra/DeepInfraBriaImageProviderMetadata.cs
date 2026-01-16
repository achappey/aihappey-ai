using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraBriaImageProviderMetadata
{
    [JsonPropertyName("speed")]
    public string? Speed { get; set; } // standard, fast, hq

    [JsonPropertyName("structured_prompt")]
    public string? StructuredPrompt { get; set; }
}

