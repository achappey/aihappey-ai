using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.TierUp;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.TierUp;

public class TierUpProviderCostingTests
{
    private const decimal ExpectedTierUpBalanceSampleCost = 0.0007935m;

    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_tierup_static_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "gen-1783724674-ZJXPen3zQI4T4dOPnHxR",
            Created = 1783724674,
            Model = "tierup-balance",
            Usage = TierUpBalanceSampleUsage()
        };

        TierUpProvider.EnrichChatCompletionWithGatewayCostForTests(response, TierUpBalancePricing());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedTierUpBalanceSampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_usage_tail_gets_finish_reason_and_gateway_cost()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "gen-1783724674-ZJXPen3zQI4T4dOPnHxR",
            Created = 1783724674,
            Model = "tierup-balance",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { content = string.Empty, role = "assistant" },
                    finish_reason = "stop",
                    native_finish_reason = "end_turn"
                }
            ]
        };

        var usageUpdate = new ChatCompletionUpdate
        {
            Id = "gen-1783724674-ZJXPen3zQI4T4dOPnHxR",
            Created = 1783724674,
            Model = "tierup-balance",
            ServiceTier = "default",
            Usage = TierUpBalanceSampleUsage()
        };

        string? lastFinishReason = null;
        TierUpProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        TierUpProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        TierUpProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, TierUpBalancePricing());

        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedTierUpBalanceSampleCost, gateway?.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("tierup")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(364, finishData.InputTokens);
        Assert.Equal(33, finishData.OutputTokens);
        Assert.Equal(397, finishData.TotalTokens);
        Assert.Equal(ExpectedTierUpBalanceSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "tierup").Single());

        Assert.Equal(364, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(33, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(397, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedTierUpBalanceSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages_metadata_shape()
    {
        var unified = new AIResponse
        {
            ProviderId = "tierup",
            Model = "tierup-balance",
            Status = "completed",
            Usage = TierUpBalanceSampleUsage()
        };

        var enriched = TierUpProvider.EnrichUnifiedResponseWithGatewayCostForTests(unified, TierUpBalancePricing());

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedTierUpBalanceSampleCost, Assert.IsType<decimal>(gateway["cost"]));

        var response = enriched.ToResponseResult();
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedTierUpBalanceSampleCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = enriched.ToMessagesResponse();
        var messagesGateway = messages.Metadata?["gateway"];
        Assert.Equal(ExpectedTierUpBalanceSampleCost, messagesGateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_cost_maps_to_responses_messages_and_api_chat_finish_metadata()
    {
        var finishEvent = TierUpProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(
            "tierup-balance",
            TierUpBalanceSampleUsage(),
            TierUpBalancePricing());

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(ExpectedTierUpBalanceSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var responseCompleted = Assert.IsType<ResponseCompleted>(finishEvent.ToResponseStreamPart());
        var responseUsage = Assert.IsType<JsonElement>(responseCompleted.Response.Usage);
        Assert.Equal(364, responseUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(33, responseUsage.GetProperty("output_tokens").GetInt32());

        var messageParts = finishEvent
            .ToMessageStreamParts()
            .ToList();
        var messageStop = Assert.Single(messageParts, part => part.Type == "message_stop");
        var messageGateway = messageStop.Metadata?["gateway"];
        Assert.Equal(ExpectedTierUpBalanceSampleCost, messageGateway?.GetProperty("cost").GetDecimal());

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "tierup").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(364, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(33, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(397, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedTierUpBalanceSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static ModelPricing TierUpBalancePricing()
        => new()
        {
            Input = 0.0000015m,
            Output = 0.0000075m
        };

    private static JsonElement TierUpBalanceSampleUsage()
        => UsageElement("""
        {
            "prompt_tokens": 364,
            "completion_tokens": 33,
            "total_tokens": 397
        }
        """);

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static int? GetUsageInt(JsonElement? usage, params string[] keys)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } usageElement)
            return null;

        foreach (var key in keys)
        {
            if (usageElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.GetInt32();
        }

        return null;
    }
}
