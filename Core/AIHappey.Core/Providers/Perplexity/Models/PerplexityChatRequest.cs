using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Perplexity;

namespace AIHappey.Core.Providers.Perplexity.Models;

public class PerplexityChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "sonar";

    [JsonPropertyName("messages")]
    public IEnumerable<PerplexityMessage> Messages { get; set; } = [];

    // Optional fields
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("search_domain_filter")]
    public List<string>? SearchDomainFilter { get; set; }

    [JsonPropertyName("return_images")]
    public bool? ReturnImages { get; set; }

    [JsonPropertyName("return_related_questions")]
    public bool? ReturnRelatedQuestions { get; set; }

    [JsonPropertyName("search_recency_filter")]
    public string? SearchRecencyFilter { get; set; }

    [JsonPropertyName("search_after_date_filter")]
    public string? SearchAfterDateFilter { get; set; }

    [JsonPropertyName("search_before_date_filter")]
    public string? SearchBeforeDateFilter { get; set; }

    [JsonPropertyName("last_updated_after_filter")]
    public string? LastUpdatedAfterFilter { get; set; }

    [JsonPropertyName("last_updated_before_filter")]
    public string? LastUpdatedBeforeFilter { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("search_mode")]
    public string? SearchMode { get; set; }

    [JsonPropertyName("enable_search_classifier")]
    public bool? EnableSearchClassifier { get; set; }

    [JsonPropertyName("web_search_options")]
    public WebSearchOptions? WebSearchOptions { get; set; }

    [JsonPropertyName("media_response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PerplexityMediaResponse? MediaResponse { get; set; }
}

public class PerplexityResultMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public class PerplexityMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public IEnumerable<IPerplexityMessageContent> Content { get; set; } = [];
}


[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PerplexityMessageContent), "text")]
[JsonDerivedType(typeof(PerplexityImageUrlContent), "image_url")]
[JsonDerivedType(typeof(PerplexityFileContent), "file_url")]
public abstract class IPerplexityMessageContent
{
}

public class PerplexityMessageContent : IPerplexityMessageContent
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class PerplexityImageUrlContent : IPerplexityMessageContent
{
    [JsonPropertyName("image_url")]
    public PerplexityUrlItem Url { get; set; } = null!;
}


public class PerplexityFileContent : IPerplexityMessageContent
{
    [JsonPropertyName("file_url")]
    public PerplexityUrlItem Url { get; set; } = null!;
}


public class PerplexityUrlItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
