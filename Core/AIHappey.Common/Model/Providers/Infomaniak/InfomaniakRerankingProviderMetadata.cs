using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Infomaniak;

public sealed class InfomaniakRerankingProviderMetadata
{
    [JsonPropertyName("max_tokens_per_doc")]
    public int? MaxTokensPerDoc { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }
}

