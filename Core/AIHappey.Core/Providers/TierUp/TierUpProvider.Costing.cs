using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TierUp;

public partial class TierUpProvider
{
    public static AIStreamEvent EnrichUnifiedFinishEventWithGatewayCostForTests(
        string? model,
        object? usage,
        ModelPricing? pricing)
        => CatalogPricingCostingExtensions.EnrichUnifiedStreamEventWithGatewayCost(
            new AIStreamEvent
            {
                ProviderId = "tierup",
                Event = new AIEventEnvelope
                {
                    Type = "finish",
                    Id = "test-finish",
                    Timestamp = DateTimeOffset.Parse("2026-07-10T23:00:00+00:00"),
                    Data = new AIFinishEventData
                    {
                        FinishReason = "stop",
                        Model = model,
                        InputTokens = GetUsageInt(usage, "prompt_tokens", "input_tokens", "promptTokens", "inputTokens"),
                        OutputTokens = GetUsageInt(usage, "completion_tokens", "output_tokens", "completionTokens", "outputTokens"),
                        TotalTokens = GetUsageInt(usage, "total_tokens", "totalTokens"),
                        MessageMetadata = AIFinishMessageMetadata.Create(
                            model ?? "tierup-balance",
                            DateTimeOffset.Parse("2026-07-10T23:00:00+00:00"),
                            usage: usage,
                            inputTokens: GetUsageInt(usage, "prompt_tokens", "input_tokens", "promptTokens", "inputTokens"),
                            outputTokens: GetUsageInt(usage, "completion_tokens", "output_tokens", "completionTokens", "outputTokens"),
                            totalTokens: GetUsageInt(usage, "total_tokens", "totalTokens"))
                    }
                }
            },
            pricing);

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

    private static int? GetUsageInt(object? usage, params string[] keys)
    {
        if (usage is null)
            return null;

        var usageElement = usage is System.Text.Json.JsonElement json
            ? json
            : System.Text.Json.JsonSerializer.SerializeToElement(usage, System.Text.Json.JsonSerializerOptions.Web);

        if (usageElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        foreach (var key in keys)
        {
            if (!usageElement.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var intValue))
                return intValue;

            if (value.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
                return intValue;
        }

        return null;
    }
}
