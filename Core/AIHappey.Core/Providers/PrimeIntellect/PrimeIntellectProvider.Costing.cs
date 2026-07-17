using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PrimeIntellect;

public partial class PrimeIntellectProvider
{
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
        CatalogPricingCostingExtensions.NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);
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

    private async Task<ChatCompletion> EnrichChatCompletionWithModelListingGatewayCostAsync(
        ChatCompletion response,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(response.Model, requestModel, cancellationToken);
        return CatalogPricingCostingExtensions.EnrichChatCompletionWithGatewayCost(response, pricing);
    }

    private async Task<ChatCompletionUpdate> EnrichChatCompletionUpdateWithModelListingGatewayCostAsync(
        ChatCompletionUpdate update,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(update.Model, requestModel, cancellationToken);
        return CatalogPricingCostingExtensions.EnrichChatCompletionUpdateWithGatewayCost(update, pricing);
    }

    private async Task<ResponseResult> EnrichResponseWithModelListingGatewayCostAsync(
        ResponseResult response,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(response.Model, requestModel, cancellationToken);
        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(response.Usage, response.Metadata, pricing);
        return response;
    }

    private async Task<MessagesResponse> EnrichMessagesResponseWithModelListingGatewayCostAsync(
        MessagesResponse response,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(response.Model, requestModel, cancellationToken);

        response.Metadata = ModelCostMetadataEnricher.AddCost(
            response.Metadata,
            ComputeMessagesCost(response.Usage, pricing));

        return response;
    }

    private async Task<MessageStreamPart> EnrichMessageStreamPartWithModelListingGatewayCostAsync(
        MessageStreamPart part,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var pricing = await ResolveModelListingPricingAsync(part.Message?.Model, requestModel, cancellationToken);

        part.Metadata = ModelCostMetadataEnricher.AddCost(
            part.Metadata,
            ComputeMessagesCost(part.Usage ?? part.Message?.Usage, pricing));

        return part;
    }

    private async Task<AIResponse> EnrichUnifiedResponseWithModelListingGatewayCostAsync(
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

    private async Task<AIStreamEvent> EnrichUnifiedStreamEventWithModelListingGatewayCostAsync(
        AIStreamEvent streamEvent,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData)
        {
            return streamEvent;
        }

        var pricing = await ResolveModelListingPricingAsync(finishData.Model, requestModel, cancellationToken);
        return CatalogPricingCostingExtensions.EnrichUnifiedStreamEventWithGatewayCost(streamEvent, pricing);
    }

    private async Task<UIMessagePart> EnrichFinishPartWithModelListingGatewayCostAsync(
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
        var fallbackPricing = this.ResolveCatalogPricing(responseModel, requestModel);
        var candidates = CatalogPricingCostingExtensions.GetPricingLookupCandidates(GetIdentifier(), responseModel, requestModel)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
            return fallbackPricing;

        try
        {
            var models = await ListModels(cancellationToken);

            foreach (var candidate in candidates)
            {
                var pricing = models
                    .FirstOrDefault(model => ModelMatchesCandidate(model, candidate))
                    ?.Pricing;

                if (pricing is not null)
                    return pricing;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return fallbackPricing;
        }

        return fallbackPricing;
    }

    private static bool ModelMatchesCandidate(Model model, string candidate)
    {
        if (string.Equals(model.Id, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model.Name, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(model.Id) || !model.Id.Contains('/', StringComparison.Ordinal))
            return false;

        var split = model.Id.SplitModelId();
        return string.Equals(split.Model, candidate, StringComparison.OrdinalIgnoreCase);
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
}
