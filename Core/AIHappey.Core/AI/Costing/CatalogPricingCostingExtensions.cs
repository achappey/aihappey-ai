using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

internal static class CatalogPricingCostingExtensions
{
    internal static ChatCompletion EnrichChatCompletionWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        ChatCompletion response,
        string? requestModel)
    {
        var pricing = modelProvider.ResolveCatalogPricing(response.Model, requestModel);
        return EnrichChatCompletionWithGatewayCost(response, pricing);
    }

    internal static ChatCompletionUpdate EnrichChatCompletionUpdateWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        ChatCompletionUpdate update,
        string? requestModel)
    {
        var pricing = modelProvider.ResolveCatalogPricing(update.Model, requestModel);
        return EnrichChatCompletionUpdateWithGatewayCost(update, pricing);
    }

    internal static ChatCompletion EnrichChatCompletionWithGatewayCost(
        ChatCompletion response,
        ModelPricing? pricing)
    {
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            response.Usage,
            pricing);

        return response;
    }

    internal static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(
        ChatCompletionUpdate update,
        ModelPricing? pricing)
    {
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            update.Usage,
            pricing);

        return update;
    }

    internal static ResponseResult EnrichResponseWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        ResponseResult response,
        string? requestModel)
    {
        var pricing = modelProvider.ResolveCatalogPricing(response.Model, requestModel);
        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(response.Usage, response.Metadata, pricing);
        return response;
    }

    internal static MessagesResponse EnrichMessagesResponseWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        MessagesResponse response,
        string? requestModel)
    {
        var pricing = modelProvider.ResolveCatalogPricing(response.Model, requestModel);

        response.Metadata = ModelCostMetadataEnricher.AddCost(
            response.Metadata,
            ComputeMessagesCost(response.Usage, pricing));

        return response;
    }

    internal static MessageStreamPart EnrichMessageStreamPartWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        MessageStreamPart part,
        string? requestModel)
    {
        var pricing = modelProvider.ResolveCatalogPricing(part.Message?.Model, requestModel);

        part.Metadata = ModelCostMetadataEnricher.AddCost(
            part.Metadata,
            ComputeMessagesCost(part.Usage ?? part.Message?.Usage, pricing));

        return part;
    }

    internal static AIResponse EnrichUnifiedResponseWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        AIResponse response,
        string? requestModel)
    {
        var pricing = modelProvider.ResolveCatalogPricing(response.Model, requestModel);
        var metadata = ModelCostMetadataEnricher.AddCostFromUsage(response.Usage, response.Metadata, pricing);

        return new AIResponse
        {
            ProviderId = response.ProviderId,
            Model = response.Model,
            Status = response.Status,
            Output = response.Output,
            Usage = response.Usage,
            Metadata = metadata
        };
    }

    internal static AIStreamEvent EnrichUnifiedStreamEventWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        AIStreamEvent streamEvent,
        string? requestModel)
    {
        if (!string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData)
        {
            return streamEvent;
        }

        var pricing = modelProvider.ResolveCatalogPricing(
            finishData.Model ?? TryGetAdditionalPropertyString(finishData.MessageMetadata?.AdditionalProperties, "model"),
            requestModel);

        return EnrichUnifiedStreamEventWithGatewayCost(streamEvent, pricing);
    }

    internal static AIStreamEvent EnrichUnifiedStreamEventWithGatewayCost(
        AIStreamEvent streamEvent,
        ModelPricing? pricing)
    {
        if (pricing is null
            || !string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData)
        {
            return streamEvent;
        }

        var metadata = finishData.MessageMetadata?.ToDictionary()
            ?? streamEvent.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            ?? [];

        if (HasGatewayCost(metadata))
            return streamEvent;

        var inputTokens = finishData.InputTokens ?? finishData.MessageMetadata?.InputTokens;
        var outputTokens = finishData.OutputTokens ?? finishData.MessageMetadata?.OutputTokens;
        var totalTokens = finishData.TotalTokens ?? finishData.MessageMetadata?.TotalTokens;

        if (inputTokens is null || inputTokens.Value <= 0)
            inputTokens = TryGetUsageInt(finishData.MessageMetadata?.Usage, "promptTokens", "prompt_tokens", "inputTokens", "input_tokens");

        if (outputTokens is null || outputTokens.Value <= 0)
            outputTokens = TryGetUsageInt(finishData.MessageMetadata?.Usage, "completionTokens", "completion_tokens", "outputTokens", "output_tokens");

        if ((outputTokens is null || outputTokens.Value <= 0) && totalTokens is > 0 && inputTokens is > 0)
            outputTokens = Math.Max(0, totalTokens.Value - inputTokens.Value);

        if ((inputTokens is null || inputTokens.Value <= 0) && (outputTokens is null || outputTokens.Value <= 0))
            return streamEvent;

        var cost = ModelCostMetadataEnricher.ComputeCost(
            pricing,
            inputTokens ?? 0,
            outputTokens ?? 0,
            cachedInputReadTokens: 0,
            cachedInputWriteTokens: 0);

        metadata = ModelCostMetadataEnricher.AddCost(metadata, cost);

        var model = finishData.Model ?? TryGetMetadataString(metadata, "model");
        var timestamp = streamEvent.Event.Timestamp ?? DateTimeOffset.UtcNow;
        var enrichedMessageMetadata = AIFinishMessageMetadata.FromDictionary(
            metadata.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value!),
            fallbackModel: model,
            fallbackTimestamp: timestamp);

        var enrichedFinishData = new AIFinishEventData
        {
            FinishReason = finishData.FinishReason,
            MessageMetadata = enrichedMessageMetadata,
            Model = finishData.Model,
            CompletedAt = finishData.CompletedAt,
            InputTokens = finishData.InputTokens,
            OutputTokens = finishData.OutputTokens,
            TotalTokens = finishData.TotalTokens,
            SequenceNumber = finishData.SequenceNumber,
            Response = finishData.Response,
            StopSequence = finishData.StopSequence
        };

        return new AIStreamEvent
        {
            ProviderId = streamEvent.ProviderId,
            Metadata = streamEvent.Metadata,
            Event = new AIEventEnvelope
            {
                Type = streamEvent.Event.Type,
                Id = streamEvent.Event.Id,
                Timestamp = streamEvent.Event.Timestamp,
                Input = streamEvent.Event.Input,
                Output = streamEvent.Event.Output,
                Metadata = streamEvent.Event.Metadata,
                Data = enrichedFinishData
            }
        };
    }

    internal static UIMessagePart EnrichFinishPartWithCatalogGatewayCost(
        this IModelProvider modelProvider,
        UIMessagePart part,
        string? requestModel)
    {
        if (part is not FinishUIPart finishPart)
            return part;

        var pricing = modelProvider.ResolveCatalogPricing(finishPart.MessageMetadata?.Model, requestModel);
        return EnrichFinishPartWithGatewayCost(finishPart, pricing);
    }

    internal static UIMessagePart EnrichFinishPartWithGatewayCost(
        FinishUIPart finishPart,
        ModelPricing? pricing)
    {
        if (pricing is null || finishPart.MessageMetadata is null || finishPart.MessageMetadata.Gateway is not null)
            return finishPart;

        var usage = finishPart.MessageMetadata.Usage;
        if (usage.PromptTokens is null && usage.CompletionTokens is null)
            return finishPart;

        var cost = ModelCostMetadataEnricher.ComputeCost(
            pricing,
            usage.PromptTokens ?? 0,
            usage.CompletionTokens ?? 0,
            cachedInputReadTokens: 0,
            cachedInputWriteTokens: 0);

        var metadata = ModelCostMetadataEnricher.AddCost(finishPart.MessageMetadata.ToDictionary(), cost);
        var enrichedMetadata = FinishMessageMetadata.FromDictionary(
            metadata.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value!),
            fallbackModel: finishPart.MessageMetadata.Model,
            fallbackTimestamp: finishPart.MessageMetadata.Timestamp);

        return new FinishUIPart
        {
            FinishReason = finishPart.FinishReason,
            MessageMetadata = enrichedMetadata
        };
    }

    internal static void NormalizeStreamingUpdateForGatewayCost(
        ChatCompletionUpdate update,
        ref string? lastFinishReason)
    {
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

    internal static ModelPricing? ResolveCatalogPricing(
        this IModelProvider modelProvider,
        string? responseModel,
        string? requestModel = null)
    {
        var pricing = modelProvider.GetIdentifier().GetPricing();
        if (pricing is null || pricing.Count == 0)
            return null;

        foreach (var candidate in GetPricingLookupCandidates(modelProvider.GetIdentifier(), responseModel, requestModel))
        {
            if (pricing.TryGetValue(candidate, out var modelPricing))
                return modelPricing;
        }

        return null;
    }

    internal static IEnumerable<string> GetPricingLookupCandidates(
        string providerId,
        string? responseModel,
        string? requestModel)
    {
        foreach (var candidate in GetSingleModelPricingLookupCandidates(providerId, responseModel))
            yield return candidate;

        foreach (var candidate in GetSingleModelPricingLookupCandidates(providerId, requestModel))
            yield return candidate;
    }

    private static IEnumerable<string> GetSingleModelPricingLookupCandidates(string providerId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            yield break;

        var trimmed = modelId.Trim();
        yield return trimmed;

        var providerPrefix = providerId + "/";
        if (!trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            yield return providerPrefix + trimmed;

        if (!trimmed.Contains('/', StringComparison.Ordinal))
            yield break;

        var split = trimmed.SplitModelId();
        if (string.IsNullOrWhiteSpace(split.Model))
            yield break;

        yield return split.Model;

        if (!split.Model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            yield return providerPrefix + split.Model;
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostToChatCompletionMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        object? usage,
        ModelPricing? pricing)
    {
        if (usage is null || pricing is null)
            return additionalProperties;

        var enrichedAdditionalProperties = additionalProperties is not null
            ? new Dictionary<string, JsonElement>(additionalProperties, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, object?>? existingMetadata = null;
        if (additionalProperties is not null
            && additionalProperties.TryGetValue("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object)
        {
            existingMetadata = metadataElement.Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web);
        }

        enrichedAdditionalProperties["metadata"] = JsonSerializer.SerializeToElement(
            ModelCostMetadataEnricher.AddCostFromUsage(usage, existingMetadata, pricing),
            JsonSerializerOptions.Web);

        return enrichedAdditionalProperties;
    }

    private static decimal? ComputeMessagesCost(MessagesUsage? usage, ModelPricing? pricing)
    {
        if (usage is null || pricing is null)
            return null;

        return ModelCostMetadataEnricher.ComputeCost(
            pricing,
            usage.InputTokens ?? 0,
            usage.OutputTokens ?? 0,
            usage.CacheReadInputTokens ?? 0,
            usage.CacheCreationInputTokens ?? 0);
    }

    private static bool HasGatewayCost(Dictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue("gateway", out var gateway) || gateway is null)
            return false;

        var gatewayElement = gateway is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(gateway, JsonSerializerOptions.Web);

        return gatewayElement.ValueKind == JsonValueKind.Object
            && gatewayElement.TryGetProperty("cost", out var costElement)
            && costElement.ValueKind == JsonValueKind.Number
            && costElement.TryGetDecimal(out _);
    }

    private static int? TryGetUsageInt(JsonElement? usage, params string[] names)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } usageElement)
            return null;

        foreach (var name in names)
        {
            if (!usageElement.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
                return intValue;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
                return intValue;
        }

        return null;
    }

    private static string? TryGetMetadataString(Dictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
    }

    private static string? TryGetAdditionalPropertyString(
        Dictionary<string, JsonElement>? additionalProperties,
        string key)
    {
        if (additionalProperties is null
            || !additionalProperties.TryGetValue(key, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string? TryGetFinishReason(ChatCompletionUpdate update)
    {
        foreach (var choice in update.Choices)
        {
            var choiceElement = JsonSerializer.SerializeToElement(choice, JsonSerializerOptions.Web);
            if (choiceElement.ValueKind != JsonValueKind.Object
                || !choiceElement.TryGetProperty("finish_reason", out var finishReasonElement)
                || finishReasonElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var finishReason = finishReasonElement.GetString();
            if (!string.IsNullOrWhiteSpace(finishReason))
                return finishReason;
        }

        return null;
    }
}
