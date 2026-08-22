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

    /// <summary>
    /// The embedding vector.
    /// Contains an array of numbers when encoding_format is "float",
    /// or a base64-encoded string when encoding_format is "base64".
    /// </summary>
    [JsonPropertyName("embedding")]
    public JsonElement Embedding { get; set; }

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
