using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Perplexity;

public sealed class PerplexityProviderMetadata
{
    [JsonPropertyName("search_mode")]
    public string? SearchMode { get; set; } // e.g. "web"

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; } // e.g. "medium"

    [JsonPropertyName("return_images")]
    public bool? ReturnImages { get; set; }

    [JsonPropertyName("return_related_questions")]
    public bool? ReturnRelatedQuestions { get; set; }

    [JsonPropertyName("search_recency_filter")]
    public string? SearchRecencyFilter { get; set; }

    [JsonPropertyName("enable_search_classifier")]
    public bool EnableSearchClassifier { get; set; }

    [JsonPropertyName("search_after_date_filter")]
    public DateTime? SearchAfterDateFilter { get; set; }

    [JsonPropertyName("search_before_date_filter")]
    public DateTime? SearchBeforeDateFilter { get; set; }

    [JsonPropertyName("last_updated_after_filter")]
    public DateTime? LastUpdatedAfterFilter { get; set; }

    [JsonPropertyName("last_updated_before_filter")]
    public DateTime? LastUpdatedBeforeFilter { get; set; }

    [JsonPropertyName("web_search_options")]
    public WebSearchOptions? WebSearchOptions { get; set; }

    [JsonPropertyName("media_response")]
    public PerplexityMediaResponse? MediaResponse { get; set; }
}

