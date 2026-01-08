using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Cohere;

public class CohereProviderMetadata
{
    [JsonPropertyName("thinking")]
    public CohereThinking? Thinking { get; set; }

    [JsonPropertyName("citation_options")]
    public CohereCitationOptions? CitationOptions { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }
}

