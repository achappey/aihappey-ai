using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.IOnet;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.IOnet;

public class IOnetProviderCostingTests
{
    private const decimal ExpectedSampleCost = 0.0003052m;

    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_ionet_model_listing_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "gen-1784111253-SMspaVdO28zVq0DTYsAG",
            Created = 1784111253,
            Model = "zai-org/GLM-4.5-Air",
            Usage = IOnetSampleUsage()
        };

        IOnetProvider.EnrichChatCompletionWithGatewayCostForTests(response, IOnetSamplePricing());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedSampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_usage_only_tail_gets_finish_reason_and_gateway_cost_for_api_chat_finish_metadata()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "gen-1784111253-SMspaVdO28zVq0DTYsAG",
            Created = 1784111253,
            Model = "zai-org/GLM-4.5-Air",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { content = string.Empty, role = "assistant" },
                    finish_reason = "stop",
                    native_finish_reason = "stop"
                }
            ]
        };

        var usageUpdate = new ChatCompletionUpdate
        {
            Id = "gen-1784111253-SMspaVdO28zVq0DTYsAG",
            Created = 1784111253,
            Model = "zai-org/GLM-4.5-Air",
            ServiceTier = null,
            Usage = IOnetSampleUsage()
        };

        string? lastFinishReason = null;
        IOnetProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        IOnetProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        IOnetProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, IOnetSamplePricing());

        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedSampleCost, gateway?.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("ionet")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(479, finishData.InputTokens);
        Assert.Equal(326, finishData.OutputTokens);
        Assert.Equal(805, finishData.TotalTokens);
        Assert.Equal(ExpectedSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "ionet").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(479, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(326, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(805, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages_metadata_shape()
    {
        var unified = new AIResponse
        {
            ProviderId = "ionet",
            Model = "zai-org/GLM-4.5-Air",
            Status = "completed",
            Usage = IOnetSampleUsage()
        };

        var enriched = IOnetProvider.EnrichUnifiedResponseWithGatewayCostForTests(unified, IOnetSamplePricing());

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedSampleCost, Assert.IsType<decimal>(gateway["cost"]));

        var response = enriched.ToResponseResult();
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedSampleCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = enriched.ToMessagesResponse();
        var messagesGateway = messages.Metadata?["gateway"];
        Assert.Equal(ExpectedSampleCost, messagesGateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_without_chat_completion_metadata_gets_gateway_cost_for_api_chat_finish_metadata()
    {
        const decimal expectedWithoutProviderCacheDetails = 0.00028275m;

        var finishEvent = new AIStreamEvent
        {
            ProviderId = "ionet",
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Timestamp = DateTimeOffset.Parse("2026-07-15T10:00:00+00:00"),
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = "zai-org/GLM-4.5-Air",
                    InputTokens = 479,
                    OutputTokens = 326,
                    TotalTokens = 805,
                    MessageMetadata = AIFinishMessageMetadata.FromDictionary(new Dictionary<string, object>
                    {
                        ["model"] = "zai-org/GLM-4.5-Air",
                        ["usage"] = new Dictionary<string, object?>
                        {
                            ["promptTokens"] = 479,
                            ["completionTokens"] = 326,
                            ["totalTokens"] = 805
                        },
                        ["ionet"] = new Dictionary<string, object?>
                        {
                            ["usage"] = IOnetSampleUsage()
                        }
                    })
                }
            }
        };

        var enriched = IOnetProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(finishEvent, IOnetSamplePricing());

        var finishData = Assert.IsType<AIFinishEventData>(enriched.Event.Data);
        Assert.Equal(expectedWithoutProviderCacheDetails, finishData.MessageMetadata?.Gateway?.Cost);

        var responseCompleted = Assert.IsType<ResponseCompleted>(enriched.ToResponseStreamPart());
        var responseUsage = Assert.IsType<JsonElement>(responseCompleted.Response.Usage);
        Assert.Equal(479, responseUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(326, responseUsage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(805, responseUsage.GetProperty("total_tokens").GetInt32());

        var messageStop = enriched
            .ToMessageStreamParts()
            .Single(part => part.Type == "message_stop");
        var messageGateway = messageStop.Metadata?["gateway"];
        Assert.Equal(expectedWithoutProviderCacheDetails, messageGateway?.GetProperty("cost").GetDecimal());

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(enriched.Event, "ionet").Single());

        Assert.Equal(expectedWithoutProviderCacheDetails, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static ModelPricing IOnetSamplePricing()
        => new()
        {
            Input = 0.00000025m,
            Output = 0.0000005m,
            InputCacheRead = 0.00000005m,
            InputCacheWrite = 0.0000002m
        };

    private static JsonElement IOnetSampleUsage()
        => UsageElement("""
        {
            "prompt_tokens": 479,
            "completion_tokens": 326,
            "total_tokens": 805,
            "prompt_tokens_details": {
                "cached_tokens": 449,
                "cache_write_tokens": 0,
                "audio_tokens": 0,
                "video_tokens": 0
            },
            "completion_tokens_details": {
                "reasoning_tokens": 171,
                "image_tokens": 0,
                "audio_tokens": 0
            }
        }
        """);

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
