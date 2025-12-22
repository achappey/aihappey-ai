using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class CohereProviderMetadata
{
    [JsonPropertyName("thinking")]
    public CohereThinking? Thinking { get; set; }

    [JsonPropertyName("citation_options")]
    public CohereCitationOptions? CitationOptions { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }
}

public class CohereCitationOptions
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "enabled"; // ENABLE,  DISABLED, FAST, ACCURATE, OFF
}

public class CohereThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "enabled";

    [JsonPropertyName("token_budget")]
    public int? TokenBudget { get; set; }


}
