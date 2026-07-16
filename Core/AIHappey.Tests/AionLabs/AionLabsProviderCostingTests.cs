using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AionLabs;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.AionLabs;

public class AionLabsProviderCostingTests
{
    private const decimal ExpectedAionMiniSampleCost = 0.00016515m;

    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_aionlabs_model_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "23040e1630f749368075f30f30dec0a5",
            Created = 1784222606,
            Model = "aion-labs/aion-3.0-mini",
            Usage = AionMiniSampleUsage()
        };

        AionLabsProvider.EnrichChatCompletionWithGatewayCostForTests(response, AionMiniPricing());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedAionMiniSampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_streaming_tail_gets_finish_reason_and_gateway_cost_for_api_chat_finish_metadata()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "23040e1630f749368075f30f30dec0a5",
            Created = 1784222606,
            Model = "aion-labs/aion-3.0-mini",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            ]
        };

        var usageUpdate = new ChatCompletionUpdate
        {
            Id = "23040e1630f749368075f30f30dec0a5",
            Created = 1784222606,
            Model = "aion-labs/aion-3.0-mini",
            Usage = AionMiniSampleUsage()
        };

        string? lastFinishReason = null;
        AionLabsProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        AionLabsProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        AionLabsProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, AionMiniPricing());

        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedAionMiniSampleCost, gateway?.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("aionlabs")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(441, finishData.InputTokens);
        Assert.Equal(165, finishData.OutputTokens);
        Assert.Equal(606, finishData.TotalTokens);
        Assert.Equal(ExpectedAionMiniSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "aionlabs").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(441, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(165, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(606, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedAionMiniSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages_metadata_shape()
    {
        var unified = new AIResponse
        {
            ProviderId = "aionlabs",
            Model = "aion-labs/aion-3.0-mini",
            Status = "completed",
            Usage = AionMiniSampleUsage()
        };

        var enriched = AionLabsProvider.EnrichUnifiedResponseWithGatewayCostForTests(unified, AionMiniPricing());

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedAionMiniSampleCost, Assert.IsType<decimal>(gateway["cost"]));

        var response = enriched.ToResponseResult();
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedAionMiniSampleCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = enriched.ToMessagesResponse();
        var messagesGateway = messages.Metadata?["gateway"];
        Assert.Equal(ExpectedAionMiniSampleCost, messagesGateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_cost_maps_to_responses_messages_and_api_chat_finish_metadata()
    {
        var finishEvent = AionLabsProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(
            new AIStreamEvent
            {
                ProviderId = "aionlabs",
                Event = new AIEventEnvelope
                {
                    Type = "finish",
                    Timestamp = DateTimeOffset.Parse("2026-07-16T17:22:06+00:00"),
                    Data = new AIFinishEventData
                    {
                        FinishReason = "stop",
                        Model = "aion-labs/aion-3.0-mini",
                        InputTokens = 441,
                        OutputTokens = 165,
                        TotalTokens = 606,
                        MessageMetadata = AIFinishMessageMetadata.FromDictionary(new Dictionary<string, object>
                        {
                            ["model"] = "aion-labs/aion-3.0-mini",
                            ["usage"] = AionMiniSampleUsage()
                        })
                    }
                }
            },
            AionMiniPricing());

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(ExpectedAionMiniSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var responseCompleted = Assert.IsType<ResponseCompleted>(finishEvent.ToResponseStreamPart());
        var responseUsage = Assert.IsType<JsonElement>(responseCompleted.Response.Usage);
        Assert.Equal(441, responseUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(165, responseUsage.GetProperty("output_tokens").GetInt32());

        var messageParts = finishEvent
            .ToMessageStreamParts()
            .ToList();
        var messageStop = Assert.Single(messageParts, part => part.Type == "message_stop");
        var messageGateway = messageStop.Metadata?["gateway"];
        Assert.Equal(ExpectedAionMiniSampleCost, messageGateway?.GetProperty("cost").GetDecimal());

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "aionlabs").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(441, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(165, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(606, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedAionMiniSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Pricing_lookup_candidates_include_aionlabs_and_aion_labs_prefixed_model_ids()
    {
        var candidates = AionLabsProvider.GetPricingLookupCandidatesForTests(
                "aion-labs/aion-3.0-mini",
                "aionlabs/aion-labs/aion-3.0-mini")
            .ToList();

        Assert.Contains("aion-labs/aion-3.0-mini", candidates);
        Assert.Contains("aionlabs/aion-labs/aion-3.0-mini", candidates);
        Assert.Contains("aion-3.0-mini", candidates);
    }

    private static ModelPricing AionMiniPricing()
        => new()
        {
            Input = 0.00000015m,
            Output = 0.0000006m,
            InputCacheRead = 0.000000015m
        };

    private static JsonElement AionMiniSampleUsage()
        => UsageElement("""
        {
            "prompt_tokens": 441,
            "completion_tokens": 165,
            "total_tokens": 606,
            "prompt_tokens_details": {
                "cached_tokens": 0
            }
        }
        """);

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
