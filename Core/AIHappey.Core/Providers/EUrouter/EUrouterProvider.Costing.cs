using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    private async Task<ChatCompletion> EnrichChatCompletionWithGatewayCostAsync(
        ChatCompletion response,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var cost = await GetGatewayCostAsync(response.Usage, response.Model, requestModel, cancellationToken);
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(response.AdditionalProperties, cost);
        return response;
    }

    private async Task<ChatCompletionUpdate> EnrichChatCompletionUpdateWithGatewayCostAsync(
        ChatCompletionUpdate update,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var cost = await GetGatewayCostAsync(update.Usage, update.Model, requestModel, cancellationToken);
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(update.AdditionalProperties, cost);
        return update;
    }

    private async Task<ResponseResult> EnrichResponseWithGatewayCostAsync(
        ResponseResult response,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var cost = await GetGatewayCostAsync(response.Usage, response.Model, requestModel, cancellationToken);
        response.Metadata = ModelCostMetadataEnricher.AddCost(response.Metadata, cost);
        return response;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(
        ChatCompletion response,
        ModelPricing? pricing)
    {
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            GetGatewayCost(response.Usage, pricing));
        return response;
    }

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(
        ChatCompletionUpdate update,
        ModelPricing? pricing)
    {
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            GetGatewayCost(update.Usage, pricing));
        return update;
    }

    public static ResponseResult EnrichResponseWithGatewayCostForTests(
        ResponseResult response,
        ModelPricing? pricing)
    {
        response.Metadata = ModelCostMetadataEnricher.AddCost(
            response.Metadata,
            GetGatewayCost(response.Usage, pricing));
        return response;
    }

    private async Task<decimal?> GetGatewayCostAsync(
        object? usage,
        string? responseModel,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var directCost = TryGetUsageCost(usage);
        if (directCost.HasValue)
            return directCost;

        var pricing = await ResolveModelPricingAsync(
            string.IsNullOrWhiteSpace(responseModel) ? requestModel : responseModel,
            requestModel,
            cancellationToken);

        return GetGatewayCost(usage, pricing);
    }

    private static decimal? GetGatewayCost(object? usage, ModelPricing? pricing)
    {
        var directCost = TryGetUsageCost(usage);
        if (directCost.HasValue)
            return directCost;

        if (pricing is null || usage is null)
            return null;

        if (!TryGetUsageInt(usage, "prompt_tokens", out var inputTokens)
            && !TryGetUsageInt(usage, "input_tokens", out inputTokens)
            && !TryGetUsageInt(usage, "promptTokens", out inputTokens)
            && !TryGetUsageInt(usage, "inputTokens", out inputTokens))
        {
            return null;
        }

        if (!TryGetUsageInt(usage, "completion_tokens", out var outputTokens)
            && !TryGetUsageInt(usage, "output_tokens", out outputTokens)
            && !TryGetUsageInt(usage, "completionTokens", out outputTokens)
            && !TryGetUsageInt(usage, "outputTokens", out outputTokens))
        {
            outputTokens = 0;
        }

        if (!TryGetUsageInt(usage, "total_tokens", out var totalTokens)
            && !TryGetUsageInt(usage, "totalTokens", out totalTokens))
        {
            totalTokens = 0;
        }

        var cachedInputReadTokens = TryGetNestedUsageInt(usage, "prompt_tokens_details", "cached_tokens", out var promptCachedTokens)
            ? promptCachedTokens
            : TryGetNestedUsageInt(usage, "input_tokens_details", "cached_tokens", out var inputCachedTokens)
                ? inputCachedTokens
                : TryGetUsageInt(usage, "cached_input_tokens", out var cachedInputTokens)
                    ? cachedInputTokens
                    : 0;

        var cachedInputWriteTokens = TryGetNestedUsageInt(usage, "prompt_tokens_details", "cache_write_tokens", out var promptCacheWriteTokens)
            ? promptCacheWriteTokens
            : TryGetNestedUsageInt(usage, "input_tokens_details", "cache_write_tokens", out var inputCacheWriteTokens)
                ? inputCacheWriteTokens
                : TryGetUsageInt(usage, "cache_write_input_tokens", out var cacheWriteInputTokens)
                    ? cacheWriteInputTokens
                    : 0;

        if (outputTokens == 0 && totalTokens > 0)
            outputTokens = Math.Max(0, totalTokens - inputTokens - cachedInputReadTokens - cachedInputWriteTokens);

        if (inputTokens <= 0 && outputTokens <= 0 && cachedInputReadTokens <= 0 && cachedInputWriteTokens <= 0)
            return null;

        return ModelCostMetadataEnricher.ComputeCost(
            pricing,
            inputTokens,
            outputTokens,
            cachedInputReadTokens,
            cachedInputWriteTokens);
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostToChatCompletionMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        decimal? cost)
    {
        if (!cost.HasValue)
            return additionalProperties;

        var enrichedAdditionalProperties = additionalProperties is not null
            ? new Dictionary<string, JsonElement>(additionalProperties, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, JsonElement>? existingMetadata = null;
        if (additionalProperties is not null
            && additionalProperties.TryGetValue("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object)
        {
            existingMetadata = metadataElement.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web);
        }

        enrichedAdditionalProperties["metadata"] = JsonSerializer.SerializeToElement(
            ModelCostMetadataEnricher.AddCost(existingMetadata, cost),
            JsonSerializerOptions.Web);

        return enrichedAdditionalProperties;
    }

    private async Task<ModelPricing?> ResolveModelPricingAsync(
        string? modelId,
        string? fallbackModelId,
        CancellationToken cancellationToken)
    {
        var listing = await GetModelsListingAsync(cancellationToken);

        foreach (var candidate in GetPricingLookupCandidates(modelId, fallbackModelId))
        {
            var model = listing.Models.FirstOrDefault(m =>
                string.Equals(m.Id, candidate, StringComparison.OrdinalIgnoreCase));

            if (model?.Pricing is not null)
                return model.Pricing;
        }

        var originalModel = await GetOriginalModelDataAsync(modelId, cancellationToken)
            ?? await GetOriginalModelDataAsync(fallbackModelId, cancellationToken);

        return TryReadPricing(originalModel);
    }

    private static IEnumerable<string> GetPricingLookupCandidates(string? modelId, string? fallbackModelId)
    {
        foreach (var candidate in GetSingleModelPricingLookupCandidates(modelId))
            yield return candidate;

        foreach (var candidate in GetSingleModelPricingLookupCandidates(fallbackModelId))
            yield return candidate;
    }

    private static IEnumerable<string> GetSingleModelPricingLookupCandidates(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            yield break;

        var trimmed = modelId.Trim();
        yield return trimmed;

        if (!trimmed.StartsWith("eurouter/", StringComparison.OrdinalIgnoreCase))
            yield return $"eurouter/{trimmed}";

        if (!trimmed.Contains('/'))
            yield break;

        var split = trimmed.SplitModelId();
        if (!string.IsNullOrWhiteSpace(split.Model))
        {
            yield return split.Model;

            if (!split.Model.StartsWith("eurouter/", StringComparison.OrdinalIgnoreCase))
                yield return $"eurouter/{split.Model}";
        }
    }

    private static ModelPricing? TryReadPricing(JsonElement? model)
    {
        if (model is null
            || model.Value.ValueKind != JsonValueKind.Object
            || !TryGetProperty(model.Value, "pricing", out var pricing)
            || pricing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = TryGetDecimal(pricing, "prompt")
            ?? TryGetDecimal(pricing, "input");
        var output = TryGetDecimal(pricing, "completion")
            ?? TryGetDecimal(pricing, "output");

        if (input is null || output is null || input.Value <= 0 || output.Value <= 0)
            return null;

        var inputCacheRead = TryGetDecimal(pricing, "input_cache_read");
        var inputCacheWrite = TryGetDecimal(pricing, "input_cache_write");

        return new ModelPricing
        {
            Input = input.Value,
            Output = output.Value,
            InputCacheRead = inputCacheRead > 0 ? inputCacheRead : null,
            InputCacheWrite = inputCacheWrite > 0 ? inputCacheWrite : null
        };
    }

    private static decimal? TryGetUsageCost(object? usage)
    {
        if (usage is null)
            return null;

        var usageElement = usage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
        };

        if (usageElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(usageElement, "cost", out var costElement))
        {
            return null;
        }

        return TryGetDecimal(costElement);
    }

    private static bool TryGetUsageInt(object usage, string key, out int value)
    {
        value = 0;

        var usageElement = usage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
        };

        return TryGetProperty(usageElement, key, out var element) && TryGetInt(element, out value);
    }

    private static bool TryGetNestedUsageInt(object usage, string parentKey, string nestedKey, out int value)
    {
        value = 0;

        var usageElement = usage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
        };

        return TryGetProperty(usageElement, parentKey, out var parent)
            && parent.ValueKind == JsonValueKind.Object
            && TryGetProperty(parent, nestedKey, out var nested)
            && TryGetInt(nested, out value);
    }

    private static bool TryGetInt(JsonElement element, out int value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var parsed) => (value = parsed) >= 0 || parsed < 0,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (value = parsed) >= 0 || parsed < 0,
            _ => false
        };
    }

    private static decimal? TryGetDecimal(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
            return null;

        return TryGetDecimal(property);
    }

    private static decimal? TryGetDecimal(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
