using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Together;

public sealed class TogetherRerankingProviderMetadata
{
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }

    [JsonPropertyName("rank_fields")]
    public IEnumerable<string>? RankFields { get; set; }
}

