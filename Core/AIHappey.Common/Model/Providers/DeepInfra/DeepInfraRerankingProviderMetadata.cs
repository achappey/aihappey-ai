using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

/// <summary>
/// Provider-specific options for DeepInfra reranking models.
/// Mirrors DeepInfra inference schema for rerank models.
/// </summary>
public sealed class DeepInfraRerankingProviderMetadata
{
    /// <summary>
    /// Instruction for the reranker model.
    /// DeepInfra default: "Given a web search query, retrieve relevant passages that answer the query".
    /// </summary>
    [JsonPropertyName("instruction")]
    public string? Instruction { get; set; }

    /// <summary>
    /// Service tier used for processing the request.
    /// Allowed values: "default" | "priority".
    /// </summary>
    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }
}

