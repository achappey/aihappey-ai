using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Augure;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Augure;

public class AugureProviderCostingTests
{
    private const decimal ExpectedOssington3SampleCost = 0.000476m;

    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_augure_catalog_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-e99a880f-3ecf-46f3-b1fc-20baef5f9668",
            Created = 1784240265,
            Model = "ossington-3",
            Usage = Ossington3SampleUsage()
        };

        AugureProvider.EnrichChatCompletionWithGatewayCostForTests(response, Ossington3Pricing());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedOssington3SampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_augure_usage_tail_gets_finish_reason_gateway_cost_and_keeps_raw_augure_metadata()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "chatcmpl-e99a880f-3ecf-46f3-b1fc-20baef5f9668",
            Created = 1784240264,
            Model = "ossington-3",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { content = "Ja man, alles top hier! 😄 En met jou? Alles chill of heb je wat nodig?" },
                    finish_reason = "stop"
                }
            ],
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["_augure"] = JsonElement("""
                {
                    "gateway_region": "ca-montreal-1",
                    "inference_region": "augure-cloud",
                    "request_id": "e99a880f-3ecf-46f3-b1fc-20baef5f9668",
                    "auto_routed": true,
                    "auto_route_reason": "default → ossington-3"
                }
                """)
            }
        };

        var usageUpdate = new ChatCompletionUpdate
        {
            Id = "chatcmpl-e99a880f-3ecf-46f3-b1fc-20baef5f9668",
            Created = 1784240265,
            Model = "ossington-3",
            Usage = Ossington3SampleUsage(),
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["_augure"] = JsonElement("""
                {
                    "gateway_region": "ca-montreal-1",
                    "inference_region": "augure-cloud",
                    "request_id": "e99a880f-3ecf-46f3-b1fc-20baef5f9668",
                    "auto_routed": true,
                    "auto_route_reason": "default → ossington-3"
                }
                """)
            }
        };

        string? lastFinishReason = null;
        AugureProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        AugureProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        AugureProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, Ossington3Pricing());

        Assert.True(usageUpdate.AdditionalProperties?.ContainsKey("_augure"));
        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedOssington3SampleCost, gateway?.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("augure")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        Assert.True(finishEvent.Metadata?.ContainsKey("chatcompletions.stream._augure"));
        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(407, finishData.InputTokens);
        Assert.Equal(23, finishData.OutputTokens);
        Assert.Equal(430, finishData.TotalTokens);
        Assert.Equal(ExpectedOssington3SampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "augure").Single());

        Assert.Equal(407, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(23, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(430, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedOssington3SampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages_metadata_shape()
    {
        var unified = new AIResponse
        {
            ProviderId = "augure",
            Model = "augure/ossington-3",
            Status = "completed",
            Usage = Ossington3SampleUsage()
        };

        var enriched = AugureProvider.EnrichUnifiedResponseWithGatewayCostForTests(unified, Ossington3Pricing());

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedOssington3SampleCost, Assert.IsType<decimal>(gateway["cost"]));

        var response = enriched.ToResponseResult();
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedOssington3SampleCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = enriched.ToMessagesResponse();
        var messagesGateway = messages.Metadata?["gateway"];
        Assert.Equal(ExpectedOssington3SampleCost, messagesGateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_cost_maps_to_responses_messages_and_api_chat_finish_metadata()
    {
        var finishEvent = AugureProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(
            new AIStreamEvent
            {
                ProviderId = "augure",
                Event = new AIEventEnvelope
                {
                    Type = "finish",
                    Id = "chatcmpl-e99a880f-3ecf-46f3-b1fc-20baef5f9668",
                    Timestamp = DateTimeOffset.Parse("2026-07-16T22:00:00+00:00"),
                    Data = new AIFinishEventData
                    {
                        FinishReason = "stop",
                        Model = "augure/ossington-3",
                        InputTokens = 407,
                        OutputTokens = 23,
                        TotalTokens = 430,
                        MessageMetadata = AIFinishMessageMetadata.Create(
                            "augure/ossington-3",
                            DateTimeOffset.Parse("2026-07-16T22:00:00+00:00"),
                            usage: Ossington3SampleUsage(),
                            inputTokens: 407,
                            outputTokens: 23,
                            totalTokens: 430)
                    }
                }
            },
            Ossington3Pricing());

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(ExpectedOssington3SampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var responseCompleted = Assert.IsType<ResponseCompleted>(finishEvent.ToResponseStreamPart());
        var responseUsage = Assert.IsType<JsonElement>(responseCompleted.Response.Usage);
        Assert.Equal(407, responseUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(23, responseUsage.GetProperty("output_tokens").GetInt32());

        var messageStop = finishEvent
            .ToMessageStreamParts()
            .Single(part => part.Type == "message_stop");
        var messageGateway = messageStop.Metadata?["gateway"];
        Assert.Equal(ExpectedOssington3SampleCost, messageGateway?.GetProperty("cost").GetDecimal());

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "augure").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(407, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(23, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(430, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedOssington3SampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static ModelPricing Ossington3Pricing()
        => new()
        {
            Input = 0.000001m,
            Output = 0.000003m
        };

    private static JsonElement Ossington3SampleUsage()
        => JsonElement("""
        {
            "prompt_tokens": 407,
            "completion_tokens": 23,
            "total_tokens": 430
        }
        """);

    private static JsonElement JsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
