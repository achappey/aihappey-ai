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
        var toolCallId = CreatePerplexityFinanceSearchToolCallId(unknown);
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

    private static AIEventEnvelope CreatePerplexityFinanceSearchOutputEnvelope(ResponseUnknownEvent unknown)
    {
        var toolCallId = CreatePerplexityFinanceSearchToolCallId(unknown);

        var results = TryGetUnknownEventProperty(unknown, "results", out var resultsElement)
            ? resultsElement.Clone()
            : JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonSerializerOptions.Web);

        var structuredContent = new Dictionary<string, object?>
        {
            ["results"] = results
        };

        if (TryGetUnknownEventProperty(unknown, "thought", out var thoughtElement)
            && thoughtElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            structuredContent["thought"] = thoughtElement.ValueKind == JsonValueKind.String
                ? thoughtElement.GetString()
                : thoughtElement.Clone();
        }

        return CreateToolOutputEnvelope(
            toolCallId,
            new ModelContextProtocol.Protocol.CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(structuredContent, JsonSerializerOptions.Web)
            },
            toolName: PerplexityFinanceSearchToolName,
            providerExecuted: true);
    }

    private static JsonElement CreatePerplexityFinanceSearchInput(ResponseUnknownEvent unknown)
    {
        var input = new Dictionary<string, object?>();

        if (TryGetUnknownEventProperty(unknown, "tickers", out var tickers))
            input["tickers"] = tickers.Clone();

        if (TryGetUnknownEventProperty(unknown, "categories", out var categories))
            input["categories"] = categories.Clone();

        if (TryGetUnknownEventProperty(unknown, "thought", out var thought)
            && thought.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            input["thought"] = thought.ValueKind == JsonValueKind.String
                ? thought.GetString()
                : thought.Clone();
        }

        return JsonSerializer.SerializeToElement(input, JsonSerializerOptions.Web);
    }

    private static string CreatePerplexityFinanceSearchToolCallId(ResponseUnknownEvent unknown)
    {
        JsonElement? categories = TryGetUnknownEventProperty(unknown, "categories", out var categoriesElement)
            ? categoriesElement
            : null;

        JsonElement? tickers = TryGetUnknownEventProperty(unknown, "tickers", out var tickersElement)
            ? tickersElement
            : null;

        if (TryGetUnknownEventProperty(unknown, "results", out var resultsElement))
        {
            categories ??= ExtractFinanceResultCategories(resultsElement);
            tickers ??= ExtractFinanceResultTickers(resultsElement);
        }

        return CreatePerplexityFinanceSearchToolCallId(categories, tickers, unknown.SequenceNumber);
    }

    private static string CreatePerplexityFinanceSearchToolCallId(ResponseStreamItem item, int outputIndex)
    {
        JsonElement? categories = item.AdditionalProperties?.TryGetValue("categories", out var categoriesElement) == true
            ? categoriesElement
            : null;

        JsonElement? tickers = item.AdditionalProperties?.TryGetValue("tickers", out var tickersElement) == true
            ? tickersElement
            : null;

        return CreatePerplexityFinanceSearchToolCallId(categories, tickers, outputIndex);
    }

    private static string CreatePerplexityFinanceSearchToolCallId(JsonElement? categories, JsonElement? tickers, int? fallback)
    {
        var tickerKey = CreateJsonArrayKey(tickers);
        var categoryKey = CreateJsonArrayKey(categories);

        if (!string.IsNullOrWhiteSpace(tickerKey) || !string.IsNullOrWhiteSpace(categoryKey))
            return $"perplexity-finance-search:{tickerKey}:{categoryKey}";

        return $"perplexity-finance-search:{fallback ?? 0}";
    }

    private static JsonElement? ExtractFinanceResultCategories(JsonElement results)
    {
        if (results.ValueKind != JsonValueKind.Array)
            return null;

        var categories = results.EnumerateArray()
            .Select(result => result.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String
                ? category.GetString()
                : null)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return categories.Length == 0
            ? null
            : JsonSerializer.SerializeToElement(categories, JsonSerializerOptions.Web);
    }

    private static JsonElement? ExtractFinanceResultTickers(JsonElement results)
    {
        if (results.ValueKind != JsonValueKind.Array)
            return null;

        var tickers = results.EnumerateArray()
            .Where(result => result.TryGetProperty("tickers", out var tickersElement)
                             && tickersElement.ValueKind == JsonValueKind.Array)
            .SelectMany(result => result.GetProperty("tickers").EnumerateArray())
            .Select(ticker => ticker.ValueKind == JsonValueKind.String ? ticker.GetString() : null)
            .Where(ticker => !string.IsNullOrWhiteSpace(ticker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tickers.Length == 0
            ? null
            : JsonSerializer.SerializeToElement(tickers, JsonSerializerOptions.Web);
    }

    private static string CreateJsonArrayKey(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array })
            return string.Empty;

        return string.Join(
            '_',
            value.Value.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim()));
    }
}
