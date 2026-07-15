using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Providers.Cortecs;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Cortecs;

public class CortecsProviderCostingTests
{
    private const decimal RawCortecsSampleCost = 902.0m;
    private const decimal ExpectedGatewaySampleCost = 0.0009020m;

    [Fact]
    public void ChatCompletion_enrichment_scales_cortecs_usage_cost_to_gateway_metadata_and_preserves_raw_usage_cost()
    {
        var response = new ChatCompletion
        {
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = CortecsSampleUsage()
        };

        CortecsProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        var usage = Assert.IsType<JsonElement>(response.Usage);
        Assert.Equal(RawCortecsSampleCost, usage.GetProperty("cost").GetDecimal());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedGatewaySampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_scales_string_cortecs_usage_cost_to_gateway_metadata()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = UsageElement("""
            {
                "completion_tokens": 29,
                "prompt_tokens": 503,
                "total_tokens": 532,
                "cost": "902.0"
            }
            """)
        };

        CortecsProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update);

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedGatewaySampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_usage_tail_gets_scaled_gateway_cost_for_unified_finish_and_api_chat_finish_metadata()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
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
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = CortecsSampleUsage()
        };

        string? lastFinishReason = null;
        CortecsProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        CortecsProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        CortecsProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate);

        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedGatewaySampleCost, gateway?.GetProperty("cost").GetDecimal());

        var rawUsage = Assert.IsType<JsonElement>(usageUpdate.Usage);
        Assert.Equal(RawCortecsSampleCost, rawUsage.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("cortecs")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(503, finishData.InputTokens);
        Assert.Equal(29, finishData.OutputTokens);
        Assert.Equal(532, finishData.TotalTokens);
        Assert.Equal(ExpectedGatewaySampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "cortecs").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(503, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(29, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(532, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedGatewaySampleCost, finishPart.MessageMetadata?.Gateway?.Cost);

        Assert.NotNull(finishPart.MessageMetadata?.ProviderMetadata);
        var providerMetadata = finishPart.MessageMetadata.ProviderMetadata;
        Assert.True(providerMetadata.TryGetValue("gateway", out var gatewayMetadata));
        Assert.Equal(ExpectedGatewaySampleCost, gatewayMetadata.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletion_enrichment_without_cortecs_usage_cost_is_noop()
    {
        var response = new ChatCompletion
        {
            Id = "5o1XatYe5sbs0w-no-cost",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = UsageElement("""
            {
                "completion_tokens": 29,
                "prompt_tokens": 503,
                "total_tokens": 532
            }
            """)
        };

        CortecsProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        Assert.Null(response.AdditionalProperties);
    }

    private static JsonElement CortecsSampleUsage()
        => UsageElement("""
        {
            "completion_tokens": 29,
            "prompt_tokens": 503,
            "total_tokens": 532,
            "completion_tokens_details": {
                "text_tokens": 29
            },
            "prompt_tokens_details": {
                "text_tokens": 503
            },
            "cost": 902.0,
            "cost_details": {
                "prompt_cost": 670,
                "cache_read_cost": 0,
                "cache_write_cost": 0,
                "completion_cost": 232,
                "prompt_audio_cost": 0
            }
        }
        """);

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

