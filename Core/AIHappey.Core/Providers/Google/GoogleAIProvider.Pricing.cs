using AIHappey.Core.AI;
using AIHappey.Interactions;
using System.Text.Json;
using AIHappey.Interactions.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static Responses.ResponseResult EnrichResponseWithGatewayCost(
        Responses.ResponseResult response,
        string? requestModel = null,
        string? requestServiceTier = null,
        Interaction? interaction = null)
    {
        var effectiveModelId = string.IsNullOrWhiteSpace(response.Model)
            ? interaction?.Model ?? interaction?.Agent ?? requestModel
            : response.Model;

        var effectiveServiceTier = string.IsNullOrWhiteSpace(response.ServiceTier)
            ? interaction?.ServiceTier ?? requestServiceTier
            : response.ServiceTier;

        var pricing = GoogleTieredPricingResolver.Resolve(
            effectiveModelId,
            effectiveServiceTier,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage));

        var normalizedUsage = NormalizeGoogleUsage(response.Usage, interaction?.Usage);

        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
            normalizedUsage,
            response.Metadata,
            pricing);

        return response;
    }

    private static object? NormalizeGoogleUsage(object? usage, InteractionUsage? interactionUsage)
    {
        if (TryNormalizeUsageFromObject(usage, out var normalizedFromUsage))
            return normalizedFromUsage;

        if (interactionUsage is null)
            return usage;

        var inputTokens = interactionUsage.TotalInputTokens;
        if (!inputTokens.HasValue)
            return usage;

        return new Dictionary<string, object?>
        {
            ["input_tokens"] = inputTokens.Value,
            ["output_tokens"] = interactionUsage.TotalOutputTokens ?? 0,
            ["total_tokens"] = interactionUsage.TotalTokens ?? 0,
            ["cached_input_tokens"] = interactionUsage.TotalCachedTokens ?? 0
        };
    }

    private static bool TryNormalizeUsageFromObject(object? usage, out Dictionary<string, object?> normalized)
    {
        normalized = [];

        if (usage is null)
            return false;

        JsonElement usageElement;

        try
        {
            usageElement = usage switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            return false;
        }

        if (usageElement.ValueKind != JsonValueKind.Object)
            return false;

        var inputTokens = TryGetUsageInt(usageElement, "input_tokens")
            ?? TryGetUsageInt(usageElement, "inputTokens")
            ?? TryGetUsageInt(usageElement, "prompt_tokens")
            ?? TryGetUsageInt(usageElement, "promptTokens")
            ?? TryGetUsageInt(usageElement, "total_input_tokens");

        if (!inputTokens.HasValue)
            return false;

        var outputTokens = TryGetUsageInt(usageElement, "output_tokens")
            ?? TryGetUsageInt(usageElement, "outputTokens")
            ?? TryGetUsageInt(usageElement, "completion_tokens")
            ?? TryGetUsageInt(usageElement, "completionTokens")
            ?? TryGetUsageInt(usageElement, "total_output_tokens")
            ?? 0;

        var totalTokens = TryGetUsageInt(usageElement, "total_tokens")
            ?? TryGetUsageInt(usageElement, "totalTokens")
            ?? 0;

        var cachedInputTokens = TryGetUsageInt(usageElement, "cached_input_tokens")
            ?? TryGetUsageInt(usageElement, "cachedInputTokens")
            ?? TryGetUsageInt(usageElement, "total_cached_tokens")
            ?? 0;

        normalized["input_tokens"] = inputTokens.Value;
        normalized["output_tokens"] = outputTokens;
        normalized["total_tokens"] = totalTokens;
        normalized["cached_input_tokens"] = cachedInputTokens;
        return true;
    }

    private static int? TryGetUsageInt(JsonElement usage, string key)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in usage.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var intValue))
                return intValue;

            if (property.Value.ValueKind == JsonValueKind.String
                && int.TryParse(property.Value.GetString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        return null;
    }

    private AIStreamEvent EnrichUnifiedFinishEventWithGatewayCost(AIStreamEvent streamEvent)
    {
        if (!string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData
            || finishData.Response is null)
        {
            return streamEvent;
        }

        Interaction? interaction;

        try
        {
            interaction = finishData.Response switch
            {
                Interaction typed => typed,
                JsonElement json when json.ValueKind == JsonValueKind.Object
                    => JsonSerializer.Deserialize<Interaction>(json.GetRawText(), JsonSerializerOptions.Web),
                _ => JsonSerializer.SerializeToElement(finishData.Response, JsonSerializerOptions.Web)
                    .Deserialize<Interaction>(JsonSerializerOptions.Web)
            };
        }
        catch
        {
            return streamEvent;
        }

        if (interaction is null)
            return streamEvent;

        var response = EnrichResponseWithGatewayCost(
            interaction.ToUnifiedResponse(GetIdentifier()).ToResponseResult(),
            requestModel: interaction.Model ?? interaction.Agent,
            requestServiceTier: interaction.ServiceTier,
            interaction: interaction);

        if (response.Metadata is null
            || !TryGetGatewayMetadata(response.Metadata, out var gateway))
        {
            return streamEvent;
        }

        var finishMetadata = finishData.MessageMetadata?.ToDictionary() ?? [];
        finishMetadata["gateway"] = gateway;

        var enrichedFinishData = new AIFinishEventData
        {
            FinishReason = finishData.FinishReason,
            MessageMetadata = AIFinishMessageMetadata.FromDictionary(
                finishMetadata,
                fallbackModel: finishData.Model ?? interaction.Model ?? interaction.Agent,
                fallbackTimestamp: DateTimeOffset.UtcNow),
            Model = finishData.Model?.ToModelId("google"),
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
                Data = enrichedFinishData,
                Metadata = streamEvent.Event.Metadata
            }
        };
    }

    private static bool TryGetGatewayMetadata(Dictionary<string, object?> metadata, out Dictionary<string, object?> gateway)
    {
        gateway = [];

        if (!metadata.TryGetValue("gateway", out var gatewayObj) || gatewayObj is null)
            return false;

        if (gatewayObj is Dictionary<string, object?> dict)
        {
            gateway = new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        try
        {
            gateway = JsonSerializer.SerializeToElement(gatewayObj, JsonSerializerOptions.Web)
                .Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web)
                ?? [];

            return gateway.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
