using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAIImageProviderMetadata
{
    [JsonPropertyName("quality")]
    public string? Quality { get; set; }
}

