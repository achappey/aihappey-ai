using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiImageProviderMetadata
{
    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("moderation")]
    public string? Moderation { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }
}

