using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Auriko;

public partial class AurikoProvider
{
    private static ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response)
    {
        var cost = GetGatewayCost(response.Usage);

        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            cost);

        return response;
    }

    private static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update)
    {
        var cost = GetGatewayCost(update.Usage);

        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            cost);

        return update;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(ChatCompletion response)
        => EnrichChatCompletionWithGatewayCost(response);

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(ChatCompletionUpdate update)
        => EnrichChatCompletionUpdateWithGatewayCost(update);

    public static AIStreamEvent EnrichUnifiedFinishEventWithGatewayCostForTests(AIStreamEvent streamEvent)
        => EnrichUnifiedFinishEventWithGatewayCost(streamEvent);

    public static UIMessagePart EnrichFinishPartWithGatewayCostForTests(UIMessagePart part)
        => EnrichFinishPartWithGatewayCost(part);

    private static UIMessagePart EnrichFinishPartWithGatewayCost(UIMessagePart part)
    {
        if (part is not FinishUIPart finishPart || finishPart.MessageMetadata is null)
            return part;

        var providerMetadata = finishPart.MessageMetadata.ProviderMetadata is not null
            ? new Dictionary<string, JsonElement>(finishPart.MessageMetadata.ProviderMetadata, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        decimal? cost = finishPart.MessageMetadata.Gateway?.Cost;
        if (!cost.HasValue
            && providerMetadata.TryGetValue(nameof(Auriko).ToLowerInvariant(), out var aurikoMetadata)
            && TryGetAurikoProperty(aurikoMetadata, "usage", out var providerUsage))
        {
            cost = GetGatewayCost(providerUsage);
        }

        // Fall back to the normalized finish usage when estimated_cost survived there.
        cost ??= GetGatewayCost(finishPart.MessageMetadata.ToDictionary()
            .TryGetValue("usage", out var usage) ? usage : null);

        if (!cost.HasValue)
            return part;

        providerMetadata["gateway"] = JsonSerializer.SerializeToElement(
            new { cost = cost.Value },
            JsonSerializerOptions.Web);

        var metadata = finishPart.MessageMetadata.ToDictionary();
        metadata["providerMetadata"] = providerMetadata;

        return new FinishUIPart
        {
            FinishReason = finishPart.FinishReason,
            MessageMetadata = FinishMessageMetadata.FromDictionary(
                metadata.Where(item => item.Value is not null)
                    .ToDictionary(item => item.Key, item => item.Value!))
        };
    }

    public static ChatCompletionUpdate NormalizeStreamingUpdateForGatewayCostForTests(
        ChatCompletionUpdate update,
        ref string? lastFinishReason)
    {
        NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);
        return update;
    }

    private static void NormalizeStreamingUpdateForGatewayCost(
        ChatCompletionUpdate update,
        ref string? lastFinishReason)
    {
        var finishReason = TryGetFinishReason(update);
        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            lastFinishReason = finishReason;
            return;
        }

        // The authoritative Auriko terminal chunk already has both finish_reason
        // and estimated_cost. Never attach the finish reason to its later routing
        // summary, because that chunk does not carry Auriko's billed estimate.
    }

    private static bool IsRoutingSummaryUsageUpdate(ChatCompletionUpdate update)
        => update.Usage is not null
            && !update.Choices.Any()
            && GetGatewayCost(update.Usage) is null;

    private static string? TryGetFinishReason(ChatCompletionUpdate update)
    {
        foreach (var choice in update.Choices)
        {
            var element = choice is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(choice, JsonSerializerOptions.Web);

            if (TryGetAurikoProperty(element, "finish_reason", out var finishReason)
                && finishReason.ValueKind == JsonValueKind.String)
            {
                return finishReason.GetString();
            }
        }

        return null;
    }

    private static AIStreamEvent EnrichUnifiedFinishEventWithGatewayCost(AIStreamEvent streamEvent)
    {
        if (!string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData)
            return streamEvent;

        var cost = GetGatewayCost(finishData.MessageMetadata?.Usage);
        if (!cost.HasValue)
            return streamEvent;

        var metadata = finishData.MessageMetadata?.ToDictionary()
            ?? streamEvent.Metadata?.ToDictionary(item => item.Key, item => item.Value)
            ?? [];

        metadata = ModelCostMetadataEnricher.AddCost(metadata, cost);
        var enrichedMessageMetadata = AIFinishMessageMetadata.FromDictionary(
            metadata.Where(item => item.Value is not null)
                .ToDictionary(item => item.Key, item => item.Value!),
            fallbackModel: finishData.Model,
            fallbackTimestamp: streamEvent.Event.Timestamp ?? DateTimeOffset.UtcNow);

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
                Data = new AIFinishEventData
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
                }
            }
        };
    }

    public static decimal? GetGatewayCost(object? usage)
    {
        if (usage is null)
            return null;

        try
        {
            var usageElement = usage switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
            };

            if (usageElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!TryGetAurikoProperty(usageElement, "estimated_cost", out var costElement)
                && !TryGetAurikoProperty(usageElement, "cost", out costElement))
                return null;

            return TryGetAurikoDecimal(costElement);
        }
        catch
        {
            return null;
        }
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

    private static decimal? TryGetAurikoDecimal(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };

    private static bool TryGetAurikoProperty(JsonElement element, string propertyName, out JsonElement value)
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
