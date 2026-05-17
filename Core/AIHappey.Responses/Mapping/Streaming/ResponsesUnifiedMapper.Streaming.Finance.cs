using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private const string PerplexityFinanceSearchToolName = "finance_search";

    private static bool IsPerplexityFinanceResultsItem(string providerId, ResponseStreamItem item)
        => IsPerplexityProvider(providerId)
           && string.Equals(item.Type, "finance_results", StringComparison.OrdinalIgnoreCase);

    private static bool IsPerplexityFinanceSearchQueriesEvent(string providerId, ResponseUnknownEvent unknown)
        => IsPerplexityProvider(providerId)
           && string.Equals(unknown.Type, "response.reasoning.finance_search_queries", StringComparison.OrdinalIgnoreCase);

    private static bool IsPerplexityFinanceSearchResultsEvent(string providerId, ResponseUnknownEvent unknown)
        => IsPerplexityProvider(providerId)
           && string.Equals(unknown.Type, "response.reasoning.finance_search_results", StringComparison.OrdinalIgnoreCase);

    private static bool IsPerplexityProvider(string providerId)
        => string.Equals(providerId, "perplexity", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<AIEventEnvelope> CreatePerplexityFinanceSearchInputEnvelopes(ResponseUnknownEvent unknown)
    {
        var toolCallId = CreatePerplexityFinanceSearchToolCallId();
        var input = CreatePerplexityFinanceSearchInput(unknown);

        yield return CreateToolInputStartEnvelope(
            toolCallId,
            PerplexityFinanceSearchToolName,
            "Finance search",
            providerExecuted: true);

        yield return CreateToolInputEndEnvelope(
            toolCallId,
            PerplexityFinanceSearchToolName,
            input,
            "Finance search",
            providerExecuted: true);
    }

    private static IEnumerable<AIEventEnvelope> CreatePerplexityFinanceSearchOutputEnvelopes(string providerId, ResponseUnknownEvent unknown)
    {
        var toolCallId = CreatePerplexityFinanceSearchToolCallId();

        var results = TryGetUnknownEventProperty(unknown, "results", out var resultsElement)
            ? resultsElement.Clone()
            : JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonSerializerOptions.Web);

        yield return CreateToolOutputEnvelope(
            toolCallId,
            new ModelContextProtocol.Protocol.CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>
                    {
                        ["results"] = results
                    },
                    JsonSerializerOptions.Web)
            },
            toolName: PerplexityFinanceSearchToolName,
            providerExecuted: true);

        foreach (var envelope in CreatePerplexityFinanceSearchReasoningEnvelopes(unknown))
            yield return envelope;

        foreach (var envelope in CreatePerplexityFinanceSearchSourceUrlEnvelopes(providerId, results, toolCallId))
            yield return envelope;
    }

    private static JsonElement CreatePerplexityFinanceSearchInput(ResponseUnknownEvent unknown)
    {
        var input = new Dictionary<string, object?>();

        if (TryGetUnknownEventProperty(unknown, "tickers", out var tickers))
            input["tickers"] = tickers.Clone();

        if (TryGetUnknownEventProperty(unknown, "categories", out var categories))
            input["categories"] = categories.Clone();

        return JsonSerializer.SerializeToElement(input, JsonSerializerOptions.Web);
    }

    private static IEnumerable<AIEventEnvelope> CreatePerplexityFinanceSearchReasoningEnvelopes(ResponseUnknownEvent unknown)
    {
        if (!TryGetUnknownEventProperty(unknown, "thought", out var thoughtElement)
            || thoughtElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            yield break;
        }

        var thought = thoughtElement.ValueKind == JsonValueKind.String
            ? thoughtElement.GetString()
            : thoughtElement.ToString();

        if (string.IsNullOrWhiteSpace(thought))
            yield break;

        var reasoningId = $"{CreatePerplexityFinanceSearchToolCallId()}:reasoning";

        yield return new AIEventEnvelope
        {
            Type = "reasoning-start",
            Id = reasoningId,
            Data = new AIReasoningStartEventData()
        };

        yield return CreateReasoningDeltaEnvelope(reasoningId, thought!);

        yield return new AIEventEnvelope
        {
            Type = "reasoning-end",
            Id = reasoningId,
            Data = new AIReasoningEndEventData()
        };
    }

    private static IEnumerable<AIEventEnvelope> CreatePerplexityFinanceSearchSourceUrlEnvelopes(
        string providerId,
        JsonElement results,
        string toolCallId)
    {
        if (results.ValueKind != JsonValueKind.Array)
            yield break;

        var sourceIndex = 0;

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var category = result.TryGetProperty("category", out var categoryElement)
                           && categoryElement.ValueKind == JsonValueKind.String
                ? categoryElement.GetString()
                : null;

            foreach (var source in sources.EnumerateArray())
            {
                var url = source.ValueKind == JsonValueKind.String
                    ? source.GetString()
                    : source.ToString();

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                sourceIndex++;

                var providerMetadata = new Dictionary<string, Dictionary<string, object>>
                {
                    [providerId] = new Dictionary<string, object>
                    {
                        ["source_type"] = "finance_search",
                        ["tool_call_id"] = toolCallId
                    }
                };

                if (!string.IsNullOrWhiteSpace(category))
                    providerMetadata[providerId]["category"] = category;

                yield return CreateSourceUrlEnvelope(
                    $"{toolCallId}:source:{sourceIndex}",
                    url,
                    url,
                    "finance_search",
                    providerMetadata: providerMetadata);
            }
        }
    }

    private static string CreatePerplexityFinanceSearchToolCallId()
        => "perplexity-finance-search";
}
