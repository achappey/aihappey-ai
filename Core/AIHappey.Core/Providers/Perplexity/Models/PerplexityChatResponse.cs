using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Perplexity.Models;

public class PerplexityChatResponse
{

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("usage")]
    public PerplexityUsage? Usage { get; set; }

    [JsonPropertyName("choices")]
    public List<PerplexityChoice>? Choices { get; set; }

    [JsonPropertyName("search_results")]
    public List<PerplexitySearchResult>? SearchResults { get; set; }

    [JsonPropertyName("videos")]
    public List<PerplexityVideoResult>? Videos { get; set; }
}

public class PerplexityVideoResult
{
    [JsonPropertyName("thumbnail_url")]
    public string? ThumbnailUrl { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = null!;

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("thumbnail_height")]
    public int? ThumbnailHeight { get; set; }

    [JsonPropertyName("thumbnail_width")]
    public int? ThumbnailWidth { get; set; }
}

public class PerplexitySearchResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = null!;

    [JsonPropertyName("url")]
    public string Url { get; set; } = null!;

    [JsonPropertyName("date")]
    public string? Date { get; set; }
}

public class PerplexityUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

    [JsonPropertyName("reasoning_tokens")]
    public int? ReasoningTokens { get; set; }

    [JsonPropertyName("citation_tokens")]
    public int? CitationTokens { get; set; }

    [JsonPropertyName("num_search_queries")]
    public int? NumSearchQueries { get; set; }

    [JsonPropertyName("search_context_size")]
    public string? SearchContextSize { get; set; }

}

public class PerplexityChoice
{
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("message")]
    public PerplexityResultMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public PerplexityResultMessage? Delta { get; set; }
}

