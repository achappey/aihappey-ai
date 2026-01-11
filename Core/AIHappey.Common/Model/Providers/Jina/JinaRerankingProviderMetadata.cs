using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Jina;

public sealed class JinaRerankingProviderMetadata
{
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }

    [JsonPropertyName("truncation")]
    public bool? Truncation { get; set; }

    [JsonPropertyName("max_doc_length")]
    public int? MaxDocLength { get; set; }

    [JsonPropertyName("return_embeddings")]
    public bool? ReturnEmbeddings { get; set; }

}

