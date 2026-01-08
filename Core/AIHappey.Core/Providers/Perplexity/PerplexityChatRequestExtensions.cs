
using System.Globalization;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Perplexity;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.Perplexity.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Perplexity;

public static class PerplexityChatRequestExtensions
{

    public static PerplexityChatRequest ToChatRequest(
        this ChatRequest chatRequest,
        IEnumerable<PerplexityMessage> messages,
        string? systemRole = null)
    {
        if (!string.IsNullOrEmpty(systemRole))
        {
            messages = [new PerplexityMessage
            {
                Role = "system",
                Content = [systemRole.ToPerplexityMessageContent()],
            }, .. messages];
        }

        static string? F(DateTime? dt) =>
            dt.HasValue ? dt.Value.ToString("M/d/yyyy", CultureInfo.InvariantCulture) : null;
        var metadata = chatRequest.GetProviderMetadata<PerplexityProviderMetadata>(nameof(Perplexity).ToLowerInvariant());
        var px = metadata;

        return new PerplexityChatRequest
        {
            Model = chatRequest.Model,
            Messages = messages,
            Temperature = chatRequest.Temperature,

            // Existing
            SearchMode = px?.SearchMode,
            ReturnImages = px?.ReturnImages,
            ReturnRelatedQuestions = px?.ReturnRelatedQuestions,
            WebSearchOptions = px?.WebSearchOptions,
            EnableSearchClassifier = px?.EnableSearchClassifier,
            MediaResponse = px?.MediaResponse,

            // NEW: Date filters (formatted) + recency
            SearchRecencyFilter = px?.SearchRecencyFilter,                 // string (e.g., "week", "day")
            SearchAfterDateFilter = F(px?.SearchAfterDateFilter),          // "M/d/yyyy"
            SearchBeforeDateFilter = F(px?.SearchBeforeDateFilter),        // "M/d/yyyy"
            LastUpdatedAfterFilter = F(px?.LastUpdatedAfterFilter),        // "M/d/yyyy"
            LastUpdatedBeforeFilter = F(px?.LastUpdatedBeforeFilter),      // "M/d/yyyy"
        };
    }

    public static PerplexityChatRequest ToChatRequest(this CreateMessageRequestParams chatRequest,
           IEnumerable<PerplexityMessage> messages,
           string? systemRole = null)
    {
        string? searchMode = null;
        if (chatRequest.Metadata is JsonElement el &&
            el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("perplexity", out var perplexityObj) &&
            perplexityObj.ValueKind == JsonValueKind.Object &&
            perplexityObj.TryGetProperty("search_mode", out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            searchMode = prop.GetString();
        }

        string? searchContextSize = null;

        if (chatRequest.Metadata is JsonElement el1 &&
            el1.ValueKind == JsonValueKind.Object &&
            el1.TryGetProperty("perplexity", out var perplexityObj1) &&
            perplexityObj1.ValueKind == JsonValueKind.Object &&
            perplexityObj1.TryGetProperty("web_search_options", out var webSearchOptionsObj) &&
            webSearchOptionsObj.ValueKind == JsonValueKind.Object &&
            webSearchOptionsObj.TryGetProperty("search_context_size", out var sizeProp) &&
            sizeProp.ValueKind == JsonValueKind.String)
        {
            searchContextSize = sizeProp.GetString();
        }


        if (!string.IsNullOrEmpty(systemRole))
        {
            messages = [new PerplexityMessage()
            {
                Role = "system",
                Content = [systemRole.ToPerplexityMessageContent()],
            }, .. messages];
        }

        return new PerplexityChatRequest()
        {
            Model = chatRequest.GetModel() ?? "sonar",
            Messages = messages,
            SearchMode = searchMode,
            WebSearchOptions = new()
            {
                SearchContextSize = Enum.Parse<SearchContextSize>(searchContextSize ?? "medium")
            },
            Temperature = chatRequest.Temperature,
        };
    }


    public static PerplexityChatRequest ToChatRequest(this string model,
           IEnumerable<PerplexityMessage> messages,
           string? systemRole = null,
           string chatReasoningEffortLevel = "medium",
           double? temperature = 1,
           JsonElement? metadata = null,
           PerplexityProviderMetadata? perplexityProviderMetadata = null)
    {
        string? searchMode = perplexityProviderMetadata?.SearchMode;
        if (metadata is JsonElement el &&
            el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("search_mode", out var prop) &&
            prop.GetString() == "academic")
        {
            searchMode = prop.GetString();
            // Found in JsonElement
        }

        if (!string.IsNullOrEmpty(systemRole))
        {
            messages = [new PerplexityMessage()
            {
                Role = "system",
                Content = [systemRole.ToPerplexityMessageContent()],
            }, .. messages];
        }

        return new PerplexityChatRequest()
        {
            Model = model,
            Messages = messages,
            SearchMode = searchMode,
            ReturnImages = perplexityProviderMetadata?.ReturnImages,
            ReturnRelatedQuestions = perplexityProviderMetadata?.ReturnRelatedQuestions,
            WebSearchOptions = perplexityProviderMetadata?.WebSearchOptions ?? new()
            {
                SearchContextSize = Enum.Parse<SearchContextSize>(chatReasoningEffortLevel)
            },
            Temperature = temperature,
        };
    }
}
