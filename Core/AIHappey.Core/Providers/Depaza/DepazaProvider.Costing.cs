using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Depaza;

public partial class DepazaProvider
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
}
