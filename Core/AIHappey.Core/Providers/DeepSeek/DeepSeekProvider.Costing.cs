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
    private const string V4FlashModel = "deepseek-v4-flash";
    private const string V4ProModel = "deepseek-v4-pro";

    private static readonly ModelPricing V4FlashOffPeakPricing = new()
    {
        Input = 0.00000022m,
        Output = 0.00000066m,
        InputCacheRead = 0.000000007m
    };

    private static readonly ModelPricing V4ProOffPeakPricing = new()
    {
        Input = 0.00000066m,
        Output = 0.00000198m,
        InputCacheRead = 0.000000022m
    };

    private ModelPricing? ResolveRuntimePricing(string? responseModel, string? requestModel)
    {
        var peakPricing = this.ResolveCatalogPricing(responseModel, requestModel);
        var model = responseModel ?? requestModel;
        return SelectRuntimePricing(model, peakPricing, DateTimeOffset.UtcNow);
    }

    private static ModelPricing? SelectRuntimePricing(
        string? model,
        ModelPricing? peakPricing,
        DateTimeOffset instant)
    {
        if (IsPeakHour(instant))
            return peakPricing;

        return NormalizeModelId(model) switch
        {
            V4FlashModel => V4FlashOffPeakPricing,
            V4ProModel => V4ProOffPeakPricing,
            _ => peakPricing
        };
    }

    private static bool IsPeakHour(DateTimeOffset instant)
    {
        var utcTime = instant.ToUniversalTime().TimeOfDay;
        return (utcTime >= TimeSpan.FromHours(1) && utcTime < TimeSpan.FromHours(4))
            || (utcTime >= TimeSpan.FromHours(6) && utcTime < TimeSpan.FromHours(10));
    }

    private static string? NormalizeModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var normalized = model.Trim();
        var separator = normalized.LastIndexOf('/');
        return (separator >= 0 ? normalized[(separator + 1)..] : normalized).ToLowerInvariant();
    }

    private ChatCompletion EnrichChatCompletionWithRuntimeGatewayCost(
        ChatCompletion response,
        string? requestModel)
        => CatalogPricingCostingExtensions.EnrichChatCompletionWithGatewayCost(
            response,
            ResolveRuntimePricing(response.Model, requestModel));

    private ChatCompletionUpdate EnrichChatCompletionUpdateWithRuntimeGatewayCost(
        ChatCompletionUpdate update,
        string? requestModel)
        => CatalogPricingCostingExtensions.EnrichChatCompletionUpdateWithGatewayCost(
            update,
            ResolveRuntimePricing(update.Model, requestModel));

    private ResponseResult EnrichResponseWithRuntimeGatewayCost(ResponseResult response, string? requestModel)
    {
        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
            response.Usage,
            response.Metadata,
            ResolveRuntimePricing(response.Model, requestModel));
        return response;
    }

    private MessagesResponse EnrichMessagesResponseWithRuntimeGatewayCost(MessagesResponse response, string? requestModel)
    {
        var pricing = ResolveRuntimePricing(response.Model, requestModel);
        var usage = response.Usage;
        var cost = usage is null || pricing is null
            ? (decimal?)null
            : ModelCostMetadataEnricher.ComputeCost(
                pricing,
                usage.InputTokens ?? 0,
                usage.OutputTokens ?? 0,
                usage.CacheReadInputTokens ?? 0,
                usage.CacheCreationInputTokens ?? 0);

        response.Metadata = ModelCostMetadataEnricher.AddCost(response.Metadata, cost);
        return response;
    }

    private MessageStreamPart EnrichMessageStreamPartWithRuntimeGatewayCost(MessageStreamPart part, string? requestModel)
    {
        var pricing = ResolveRuntimePricing(part.Message?.Model, requestModel);
        var usage = part.Usage ?? part.Message?.Usage;
        var cost = usage is null || pricing is null
            ? (decimal?)null
            : ModelCostMetadataEnricher.ComputeCost(
                pricing,
                usage.InputTokens ?? 0,
                usage.OutputTokens ?? 0,
                usage.CacheReadInputTokens ?? 0,
                usage.CacheCreationInputTokens ?? 0);

        part.Metadata = ModelCostMetadataEnricher.AddCost(part.Metadata, cost);
        return part;
    }

    private AIResponse EnrichUnifiedResponseWithRuntimeGatewayCost(AIResponse response, string? requestModel)
        => EnrichUnifiedResponseWithGatewayCostForTests(
            response,
            ResolveRuntimePricing(response.Model, requestModel));

    private AIStreamEvent EnrichUnifiedStreamEventWithRuntimeGatewayCost(AIStreamEvent streamEvent, string? requestModel)
    {
        if (!string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData)
        {
            return streamEvent;
        }

        return CatalogPricingCostingExtensions.EnrichUnifiedStreamEventWithGatewayCost(
            streamEvent,
            ResolveRuntimePricing(finishData.Model, requestModel));
    }

    private UIMessagePart EnrichFinishPartWithRuntimeGatewayCost(UIMessagePart part, string? requestModel)
    {
        if (part is not FinishUIPart finishPart)
            return part;

        return CatalogPricingCostingExtensions.EnrichFinishPartWithGatewayCost(
            finishPart,
            ResolveRuntimePricing(finishPart.MessageMetadata?.Model, requestModel));
    }

    public static ModelPricing? SelectRuntimePricingForTests(
        string? model,
        ModelPricing? peakPricing,
        DateTimeOffset instant)
        => SelectRuntimePricing(model, peakPricing, instant);

    public static bool IsPeakHourForTests(DateTimeOffset instant) => IsPeakHour(instant);

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
