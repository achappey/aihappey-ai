using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.IOnet;

public partial class IOnetProvider
{
    private async Task<ChatCompletion> EnrichChatCompletionWithGatewayCostAsync(
        ChatCompletion response,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(response.Model, requestModel, cancellationToken);
        return CatalogPricingCostingExtensions.EnrichChatCompletionWithGatewayCost(response, pricing);
    }

    private async Task<ChatCompletionUpdate> EnrichChatCompletionUpdateWithGatewayCostAsync(
        ChatCompletionUpdate update,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(update.Model, requestModel, cancellationToken);
        return CatalogPricingCostingExtensions.EnrichChatCompletionUpdateWithGatewayCost(update, pricing);
    }

    private async Task<AIResponse> EnrichUnifiedResponseWithGatewayCostAsync(
        AIResponse response,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(response.Model, requestModel, cancellationToken);
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

    private async Task<AIStreamEvent> EnrichUnifiedStreamEventWithGatewayCostAsync(
        AIStreamEvent streamEvent,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData)
        {
            return streamEvent;
        }

        var pricing = await ResolveModelListingPricingAsync(
            finishData.Model ?? TryGetAdditionalPropertyString(finishData.MessageMetadata, "model"),
            requestModel,
            cancellationToken);

        return CatalogPricingCostingExtensions.EnrichUnifiedStreamEventWithGatewayCost(streamEvent, pricing);
    }

    private async Task<UIMessagePart> EnrichFinishPartWithGatewayCostAsync(
        UIMessagePart part,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        if (part is not FinishUIPart finishPart)
            return part;

        var pricing = await ResolveModelListingPricingAsync(finishPart.MessageMetadata?.Model, requestModel, cancellationToken);
        return CatalogPricingCostingExtensions.EnrichFinishPartWithGatewayCost(finishPart, pricing);
    }

    private async Task<ModelPricing?> ResolveModelListingPricingAsync(
        string? responseModel,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in CatalogPricingCostingExtensions.GetPricingLookupCandidates(GetIdentifier(), responseModel, requestModel))
        {
            try
            {
                var model = await this.GetModel(candidate, cancellationToken);
                if (model.Pricing is not null)
                    return model.Pricing;
            }
            catch (ArgumentException)
            {
                // Try the next normalized/provider-prefixed candidate.
            }
        }

        return null;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(
        ChatCompletion response,
        ModelPricing? pricing)
        => CatalogPricingCostingExtensions.EnrichChatCompletionWithGatewayCost(response, pricing);

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(
        ChatCompletionUpdate update,
        ModelPricing? pricing)
        => CatalogPricingCostingExtensions.EnrichChatCompletionUpdateWithGatewayCost(update, pricing);

    public static ChatCompletionUpdate NormalizeStreamingUpdateForGatewayCostForTests(
        ChatCompletionUpdate update,
        ref string? lastFinishReason)
    {
        NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);
        return update;
    }

    public static AIResponse EnrichUnifiedResponseWithGatewayCostForTests(
        AIResponse response,
        ModelPricing? pricing)
    {
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

    public static AIStreamEvent EnrichUnifiedFinishEventWithGatewayCostForTests(
        AIStreamEvent streamEvent,
        ModelPricing? pricing)
        => CatalogPricingCostingExtensions.EnrichUnifiedStreamEventWithGatewayCost(streamEvent, pricing);

    public static UIMessagePart EnrichFinishPartWithGatewayCostForTests(
        FinishUIPart finishPart,
        ModelPricing? pricing)
        => CatalogPricingCostingExtensions.EnrichFinishPartWithGatewayCost(finishPart, pricing);

    private static void NormalizeStreamingUpdateForGatewayCost(
        ChatCompletionUpdate update,
        ref string? lastFinishReason)
        => CatalogPricingCostingExtensions.NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);

    private static string? TryGetAdditionalPropertyString(
        AIFinishMessageMetadata? metadata,
        string key)
    {
        if (metadata?.AdditionalProperties is null
            || !metadata.AdditionalProperties.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
