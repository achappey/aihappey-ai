using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

/// <summary>OpenAI-compatible request for POST /v1/embeddings.</summary>
public sealed class OpenAIEmbeddingRequest
{
    [JsonPropertyName("input")]
    public JsonElement Input { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("dimensions")]
    public int? Dimensions { get; set; }

    [JsonPropertyName("encoding_format")]
    public string? EncodingFormat { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

public sealed class OpenAIEmbeddingResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public IEnumerable<OpenAIEmbeddingData> Data { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("usage")]
    public OpenAIEmbeddingUsage Usage { get; set; } = new();
}

public sealed class OpenAIEmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "embedding";

    /// <summary>An array of numbers for float encoding, or a base64 string.</summary>
    [JsonPropertyName("embedding")]
    public object Embedding { get; set; } = null!;

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

public sealed class OpenAIEmbeddingUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}
