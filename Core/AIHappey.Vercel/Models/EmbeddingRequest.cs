using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>AI SDK EmbeddingModelV4 wire request for POST /api/embeddings.</summary>
public sealed class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("values")]
    public IEnumerable<string> Values { get; set; } = [];

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }
}

public sealed class EmbeddingResponse
{
    [JsonPropertyName("embeddings")]
    public IEnumerable<IEnumerable<float>> Embeddings { get; set; } = [];

    [JsonPropertyName("usage")]
    public EmbeddingUsage? Usage { get; set; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    [JsonPropertyName("response")]
    public EmbeddingResponseMetadata? Response { get; set; }

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];
}

public sealed class EmbeddingUsage
{
    [JsonPropertyName("tokens")]
    public int Tokens { get; set; }
}

public sealed class EmbeddingResponseMetadata
{
    [JsonPropertyName("headers")]
    public IDictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public object? Body { get; set; }
}
