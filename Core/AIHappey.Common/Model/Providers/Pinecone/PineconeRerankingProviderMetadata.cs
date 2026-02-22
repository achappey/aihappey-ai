using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Pinecone;

public sealed class PineconeRerankingProviderMetadata
{
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }

    [JsonPropertyName("rank_fields")]
    public IEnumerable<string>? RankFields { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}
