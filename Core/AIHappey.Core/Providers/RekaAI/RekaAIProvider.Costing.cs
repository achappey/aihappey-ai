using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    private const string RekaFlashResearchModelId = "reka-flash-research";
    private const string DefaultResponseModelId = "default";

    private static readonly RekaResearchRequestPricing RekaFlashResearchRequestPricing = new(
        Standard: 0.025m,
        ParallelThinkingLow: 0.035m,
        ParallelThinkingHigh: 0.060m);

    private async Task<ChatCompletion> EnrichChatCompletionWithGatewayCostAsync(
        ChatCompletion response,
        ChatCompletionOptions requestOptions,
        CancellationToken cancellationToken)
    {
        var cost = await GetGatewayCostAsync(
            response.Usage,
            response.Model,
            requestOptions,
            cancellationToken);

        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(response.AdditionalProperties, cost);
        return response;
    }

    private async Task<ChatCompletionUpdate> EnrichChatCompletionUpdateWithGatewayCostAsync(
        ChatCompletionUpdate update,
        ChatCompletionOptions requestOptions,
        CancellationToken cancellationToken)
    {
        var cost = await GetGatewayCostAsync(
            update.Usage,
            update.Model,
            requestOptions,
            cancellationToken);

        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(update.AdditionalProperties, cost);
        return update;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(
        ChatCompletion response,
        ModelPricing? pricing,
        ChatCompletionOptions? requestOptions = null)
    {
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            GetGatewayCost(response.Usage, response.Model, requestOptions, pricing));
        return response;
    }

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(
        ChatCompletionUpdate update,
        ModelPricing? pricing,
        ChatCompletionOptions? requestOptions = null)
    {
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            GetGatewayCost(update.Usage, update.Model, requestOptions, pricing));
        return update;
    }

    public static ChatCompletionUpdate NormalizeStreamingUpdateForGatewayCostForTests(
        ChatCompletionUpdate update,
        ChatCompletionOptions requestOptions,
        ref string? lastFinishReason)
    {
        NormalizeStreamingUpdateForGatewayCost(update, requestOptions, ref lastFinishReason);
        return update;
    }

    private static void NormalizeStreamingUpdateForGatewayCost(
        ChatCompletionUpdate update,
        ChatCompletionOptions requestOptions,
        ref string? lastFinishReason)
    {
        if (ShouldUseRequestModel(update.Model))
            update.Model = requestOptions.Model;

        var finishReason = TryGetFinishReason(update);
        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            lastFinishReason = finishReason;
            return;
        }

        if (update.Usage is null || update.Choices.Any() || string.IsNullOrWhiteSpace(lastFinishReason))
            return;

        update.Choices =
        [
            new
            {
                index = 0,
                delta = new { },
                finish_reason = lastFinishReason
            }
        ];
    }

    private async Task<decimal?> GetGatewayCostAsync(
        object? usage,
        string? responseModel,
        ChatCompletionOptions requestOptions,
        CancellationToken cancellationToken)
    {
        var effectiveModel = ResolveCostModelId(responseModel, requestOptions.Model);
        var pricing = await ResolveModelPricingAsync(
            effectiveModel,
            requestOptions.Model,
            cancellationToken);

        return GetGatewayCost(usage, effectiveModel, requestOptions, pricing);
    }

    private static decimal? GetGatewayCost(
        object? usage,
        string? responseModel,
        ChatCompletionOptions? requestOptions,
        ModelPricing? pricing)
    {
        var modelId = ResolveCostModelId(responseModel, requestOptions?.Model);

        if (IsRekaFlashResearchModel(modelId))
            return RekaFlashResearchRequestPricing.GetCost(ResolveResearchPricingTier(requestOptions));

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

    private async Task<ModelPricing?> ResolveModelPricingAsync(
        string? modelId,
        string? fallbackModelId,
        CancellationToken cancellationToken)
    {
        if (IsRekaFlashResearchModel(modelId) || IsRekaFlashResearchModel(fallbackModelId))
            return null;

        var models = await ListModels(cancellationToken);

        foreach (var candidate in GetPricingLookupCandidates(modelId, fallbackModelId))
        {
            var model = models.FirstOrDefault(m =>
                string.Equals(m.Id, candidate, StringComparison.OrdinalIgnoreCase));

            if (model?.Pricing is not null)
                return model.Pricing;
        }

        return null;
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

        if (!trimmed.StartsWith("rekaai/", StringComparison.OrdinalIgnoreCase))
            yield return $"rekaai/{trimmed}";

        if (!trimmed.Contains('/', StringComparison.Ordinal))
            yield break;

        var split = trimmed.SplitModelId();
        if (string.IsNullOrWhiteSpace(split.Model))
            yield break;

        yield return split.Model;

        if (!split.Model.StartsWith("rekaai/", StringComparison.OrdinalIgnoreCase))
            yield return $"rekaai/{split.Model}";
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

    private static string? ResolveResearchPricingTier(ChatCompletionOptions? requestOptions)
    {
        var tier = NormalizeResearchPricingTier(requestOptions?.ReasoningEffort);
        if (tier is not null)
            return tier;

        if (requestOptions?.AdditionalProperties is null)
            return null;

        foreach (var propertyName in ResearchPricingTierOptionNames)
        {
            if (!requestOptions.AdditionalProperties.TryGetValue(propertyName, out var value))
                continue;

            tier = ResolveResearchPricingTier(value);
            if (tier is not null)
                return tier;
        }

        return null;
    }

    private static string? ResolveResearchPricingTier(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => NormalizeResearchPricingTier(element.GetString()),
            JsonValueKind.Object => ResolveResearchPricingTier(element, ResearchPricingTierNestedOptionNames),
            _ => null
        };

    private static string? ResolveResearchPricingTier(JsonElement element, IReadOnlyCollection<string> propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var property))
                continue;

            var tier = ResolveResearchPricingTier(property);
            if (tier is not null)
                return tier;
        }

        return null;
    }

    private static string? NormalizeResearchPricingTier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToLowerInvariant().Replace("_", "-");
        return normalized switch
        {
            "low" or "parallel-thinking-low" or "parallel-low" => "low",
            "high" or "parallel-thinking-high" or "parallel-high" => "high",
            _ => null
        };
    }

    private static bool IsRekaFlashResearchModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var trimmed = modelId.Trim();
        if (string.Equals(trimmed, RekaFlashResearchModelId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, $"rekaai/{RekaFlashResearchModelId}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!trimmed.Contains('/', StringComparison.Ordinal))
            return false;

        var split = trimmed.SplitModelId();
        return string.Equals(split.Model, RekaFlashResearchModelId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCostModelId(string? responseModel, string? requestModel)
        => ShouldUseRequestModel(responseModel) ? requestModel : responseModel;

    private static bool ShouldUseRequestModel(string? responseModel)
        => string.IsNullOrWhiteSpace(responseModel)
            || string.Equals(responseModel.Trim(), DefaultResponseModelId, StringComparison.OrdinalIgnoreCase);

    private static string? TryGetFinishReason(ChatCompletionUpdate update)
    {
        foreach (var choice in update.Choices)
        {
            var choiceElement = choice switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(choice, JsonSerializerOptions.Web)
            };

            if (!TryGetProperty(choiceElement, "finish_reason", out var finishReasonElement)
                || finishReasonElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var finishReason = finishReasonElement.GetString();
            if (!string.IsNullOrWhiteSpace(finishReason)
                && !string.Equals(finishReason, "thinking_end", StringComparison.OrdinalIgnoreCase))
            {
                return finishReason;
            }
        }

        return null;
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

    private static readonly string[] ResearchPricingTierOptionNames =
    [
        "parallel_thinking",
        "parallelThinking",
        "parallel_thinking_level",
        "parallelThinkingLevel",
        "parallel_thinking_mode",
        "parallelThinkingMode",
        "thinking_level",
        "thinkingLevel",
        "thinking"
    ];

    private static readonly string[] ResearchPricingTierNestedOptionNames =
    [
        "level",
        "mode",
        "tier",
        "effort"
    ];

    private sealed record RekaResearchRequestPricing(
        decimal Standard,
        decimal ParallelThinkingLow,
        decimal ParallelThinkingHigh)
    {
        public decimal GetCost(string? tier)
            => tier switch
            {
                "low" => ParallelThinkingLow,
                "high" => ParallelThinkingHigh,
                _ => Standard
            };
    }
}
