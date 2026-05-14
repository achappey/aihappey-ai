using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Tavily;

/// <summary>
/// ProviderOptions schema for Tavily research requests.
/// Consumed via <c>providerOptions.tavily</c> for the unified Tavily research flow.
/// </summary>
public sealed class TavilyProviderMetadata
{
    [JsonPropertyName("citation_format")]
    public string? CitationFormat { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
