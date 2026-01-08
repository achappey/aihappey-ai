using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Cohere;

public class CohereThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "enabled";

    [JsonPropertyName("token_budget")]
    public int? TokenBudget { get; set; }
}

