using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Fireworks;

/// <summary>
/// Provider-specific options for Fireworks reranking.
/// Mirrors Fireworks POST /rerank schema.
/// </summary>
public sealed class FireworksRerankingProviderMetadata
{
    /// <summary>
    /// Optional task description to guide the reranking process.
    /// Fireworks example: "Given a web search query, retrieve relevant passages that answer the query".
    /// </summary>
    [JsonPropertyName("task")]
    public string? Task { get; set; }

    /// <summary>
    /// Whether to return the document text in the response.
    /// Defaults to true in Fireworks; you can set to false to reduce response size.
    /// </summary>
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }
}

