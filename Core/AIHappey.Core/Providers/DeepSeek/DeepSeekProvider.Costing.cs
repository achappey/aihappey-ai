using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
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

    public static ResponseResult EnrichResponseWithGatewayCostForTests(
        ResponseResult response,
        ModelPricing? pricing)
    {
        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(response.Usage, response.Metadata, pricing);
        return response;
    }

    public static MessagesResponse EnrichMessagesResponseWithGatewayCostForTests(
        MessagesResponse response,
        ModelPricing? pricing)
    {
        var usage = response.Usage;
        decimal? cost = usage is null || pricing is null
       ? null
       : ModelCostMetadataEnricher.ComputeCost(
           pricing,
           usage.InputTokens ?? 0,
           usage.OutputTokens ?? 0,
           usage.CacheReadInputTokens ?? 0,
           usage.CacheCreationInputTokens ?? 0);

        response.Metadata = ModelCostMetadataEnricher.AddCost(response.Metadata, cost);
        return response;
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
}
