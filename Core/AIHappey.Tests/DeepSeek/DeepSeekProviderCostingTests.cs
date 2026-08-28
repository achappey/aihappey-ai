using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.DeepSeek;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.DeepSeek;

public class DeepSeekProviderCostingTests
{
    private const decimal ExpectedSampleCost = 0.000407595m;

    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_deepseek_pricing()
    {
        var response = new ChatCompletion
        {
            Model = "deepseek-v4-pro",
            Usage = SampleUsage()
        };

        DeepSeekProvider.EnrichChatCompletionWithGatewayCostForTests(response, ProPricing());

        Assert.Equal(ExpectedSampleCost,
            response.AdditionalProperties?["metadata"].GetProperty("gateway").GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_terminal_usage_gets_gateway_cost_and_maps_to_unified_and_api_chat()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "32dc3b19-769a-472b-8ffe-9c90d2b0c0cc",
            Created = 1786275483,
            Model = "deepseek-v4-pro",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { content = "", reasoning_content = (string?)null },
                    finish_reason = "stop"
                }
            ],
            Usage = SampleUsage()
        };

        string? lastFinishReason = null;
        DeepSeekProvider.NormalizeStreamingUpdateForGatewayCostForTests(update, ref lastFinishReason);
        DeepSeekProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, ProPricing());

        Assert.Equal("stop", lastFinishReason);
        Assert.Equal(ExpectedSampleCost,
            update.AdditionalProperties?["metadata"].GetProperty("gateway").GetProperty("cost").GetDecimal());

        var finishEvent = update.ToUnifiedStreamEvents("deepseek")
            .Single(item => item.Event.Type == "finish");
        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(441, finishData.InputTokens);
        Assert.Equal(248, finishData.OutputTokens);
        Assert.Equal(689, finishData.TotalTokens);
        Assert.Equal(ExpectedSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "deepseek").Single());
        Assert.Equal(441, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(248, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(ExpectedSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void ChatCompletion_cache_hits_use_deepseek_cache_read_price()
    {
        var response = new ChatCompletion
        {
            Model = "deepseek-v4-pro",
            Usage = UsageElement("""
            {
                "prompt_tokens": 441,
                "completion_tokens": 248,
                "total_tokens": 689,
                "prompt_tokens_details": { "cached_tokens": 100 },
                "prompt_cache_hit_tokens": 100,
                "prompt_cache_miss_tokens": 341
            }
            """)
        };

        DeepSeekProvider.EnrichChatCompletionWithGatewayCostForTests(response, ProPricing());

        Assert.Equal(0.0004079575m,
            response.AdditionalProperties?["metadata"].GetProperty("gateway").GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Native_responses_and_messages_get_gateway_cost()
    {
        var response = DeepSeekProvider.EnrichResponseWithGatewayCostForTests(new ResponseResult
        {
            Model = "deepseek-v4-pro",
            Usage = SampleUsage()
        }, ProPricing());
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedSampleCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = DeepSeekProvider.EnrichMessagesResponseWithGatewayCostForTests(new MessagesResponse
        {
            Model = "deepseek-v4-pro",
            Usage = new MessagesUsage { InputTokens = 441, OutputTokens = 248 }
        }, ProPricing());
        Assert.Equal(ExpectedSampleCost,
            messages.Metadata?["gateway"].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages()
    {
        var enriched = DeepSeekProvider.EnrichUnifiedResponseWithGatewayCostForTests(new AIResponse
        {
            ProviderId = "deepseek",
            Model = "deepseek/deepseek-v4-pro",
            Status = "completed",
            Usage = SampleUsage()
        }, ProPricing());

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedSampleCost, Assert.IsType<decimal>(gateway["cost"]));

        var responsesGateway = Assert.IsType<Dictionary<string, object?>>(enriched.ToResponseResult().Metadata?["gateway"]);
        Assert.Equal(ExpectedSampleCost, Assert.IsType<decimal>(responsesGateway["cost"]));
        Assert.Equal(ExpectedSampleCost,
            enriched.ToMessagesResponse().Metadata?["gateway"].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_cost_maps_to_responses_messages_and_api_chat()
    {
        var finishEvent = DeepSeekProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(new AIStreamEvent
        {
            ProviderId = "deepseek",
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = "32dc3b19-769a-472b-8ffe-9c90d2b0c0cc",
                Timestamp = DateTimeOffset.Parse("2026-08-09T11:38:03Z"),
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = "deepseek/deepseek-v4-pro",
                    InputTokens = 441,
                    OutputTokens = 248,
                    TotalTokens = 689,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        "deepseek/deepseek-v4-pro",
                        DateTimeOffset.Parse("2026-08-09T11:38:03Z"),
                        usage: SampleUsage(),
                        inputTokens: 441,
                        outputTokens: 248,
                        totalTokens: 689)
                }
            }
        }, ProPricing());

        var data = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(ExpectedSampleCost, data.MessageMetadata?.Gateway?.Cost);
        Assert.IsType<ResponseCompleted>(finishEvent.ToResponseStreamPart(
            new ResponsesUnifiedMapper.ResponseReverseStreamState()));
     
        Assert.Equal(ExpectedSampleCost, Assert.IsType<FinishUIPart>(
            finishEvent.Event.ToUIMessagePart("deepseek").Single()).MessageMetadata?.Gateway?.Cost);
    }

    private static ModelPricing ProPricing() => new()
    {
        Input = 0.000000435m,
        Output = 0.00000087m,
        InputCacheRead = 0.000000003625m
    };

    private static JsonElement SampleUsage() => UsageElement("""
    {
        "prompt_tokens": 441,
        "completion_tokens": 248,
        "total_tokens": 689,
        "prompt_tokens_details": { "cached_tokens": 0 },
        "completion_tokens_details": { "reasoning_tokens": 237 },
        "prompt_cache_hit_tokens": 0,
        "prompt_cache_miss_tokens": 441
    }
    """);

    private static JsonElement UsageElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
