using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Perplexity;

public sealed class WebSearchOptions
{
    [JsonPropertyName("search_context_size")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SearchContextSize SearchContextSize { get; set; } = SearchContextSize.medium;

    [JsonPropertyName("user_location")]
    public PerplexityUserLocation? UserLocation { get; set; }

    [JsonPropertyName("image_search_relevance_enhanced")]
    public bool ImageSearchRelevanceEnhanced { get; set; }

    [JsonPropertyName("search_type")]
    public string? SearchType { get; set; } // pro or fast

}

