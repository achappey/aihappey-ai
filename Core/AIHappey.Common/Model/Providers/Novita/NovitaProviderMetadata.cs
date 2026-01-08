using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Novita;

public class NovitaProviderMetadata
{
    [JsonPropertyName("separate_reasoning")]
    public bool? SeparateReasoning { get; set; }

    [JsonPropertyName("enable_thinking")]
    public bool? EnableThinking { get; set; }
}

