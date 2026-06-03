using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.RekaAI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.RekaAI;

public class RekaAIProviderCostingTests
{
    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_reka_chat_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-reka-1",
            Created = 1777285265,
            Model = "rekaai/reka-flash-3",
            Usage = UsageElement("""
            {
                "prompt_tokens": 1500,
                "completion_tokens": 250,
                "total_tokens": 1750
            }
            """)
        };

        RekaAIProvider.EnrichChatCompletionWithGatewayCostForTests(response, new ModelPricing
        {
            Input = 0.0000008m,
            Output = 0.000002m
        });

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.0017m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_calculates_gateway_cost_from_reka_edge_pricing_with_cached_tokens()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "chatcmpl-reka-2",
            Created = 1777285265,
            Model = "rekaai/reka-edge-2603",
            Usage = UsageElement("""
            {
                "prompt_tokens": 1000,
                "completion_tokens": 500,
                "total_tokens": 1500,
                "prompt_tokens_details": {
                    "cached_tokens": 100
                }
            }
            """)
        };

        RekaAIProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.0000001m,
            Output = 0.0000001m,
            InputCacheRead = 0.00000002m
        });

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.000152m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Research_model_enrichment_uses_static_standard_request_pricing_without_usage()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-reka-research-1",
            Created = 1777285265,
            Model = "rekaai/reka-flash-research"
        };

        RekaAIProvider.EnrichChatCompletionWithGatewayCostForTests(response, pricing: null);

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.025m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Research_model_enrichment_uses_static_parallel_thinking_high_request_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-reka-research-2",
            Created = 1777285265,
            Model = "reka-flash-research"
        };

        var requestOptions = new ChatCompletionOptions
        {
            Model = "rekaai/reka-flash-research",
            AdditionalProperties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["parallel_thinking"] = JsonSerializer.SerializeToElement("high", JsonSerializerOptions.Web)
            }
        };

        RekaAIProvider.EnrichChatCompletionWithGatewayCostForTests(response, pricing: null, requestOptions);

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.060m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_finish_gateway_cost_propagates_to_unified_and_vercel_ui_finish_metadata()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "chatcmpl-reka-3",
            Created = 1777285265,
            Model = "rekaai/reka-flash-3",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            ],
            Usage = UsageElement("""
            {
                "prompt_tokens": 1500,
                "completion_tokens": 250,
                "total_tokens": 1750
            }
            """)
        };

        RekaAIProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.0000008m,
            Output = 0.000002m
        });

        var finishEvent = update
            .ToUnifiedStreamEvents("rekaai")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(0.0017m, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "rekaai").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(1500, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(250, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(1750, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.0017m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Streaming_default_model_usage_only_chunk_uses_request_model_and_emits_costed_finish_metadata()
    {
        var requestOptions = new ChatCompletionOptions
        {
            Model = "rekaai/reka-flash-3"
        };

        string? lastFinishReason = null;
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "chatcmpl-reka-default-1",
            Created = 1780494289,
            Model = "default",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { content = "yond.\n\n" },
                    finish_reason = "stop"
                }
            ]
        };

        RekaAIProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, requestOptions, ref lastFinishReason);

        var usageUpdate = new ChatCompletionUpdate
        {
            Id = "chatcmpl-reka-default-1",
            Created = 1780494289,
            Model = "default",
            Choices = [],
            Usage = UsageElement("""
            {
                "completion_tokens": 671,
                "prompt_tokens": 446,
                "total_tokens": 1117,
                "prompt_tokens_details": {
                    "audio_tokens": 0,
                    "cached_tokens": 272
                }
            }
            """)
        };

        RekaAIProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, requestOptions, ref lastFinishReason);
        RekaAIProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, new ModelPricing
        {
            Input = 0.0000008m,
            Output = 0.000002m
        }, requestOptions);

        Assert.Equal("rekaai/reka-flash-3", usageUpdate.Model);
        Assert.NotEmpty(usageUpdate.Choices);

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("rekaai")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "rekaai").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(446, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(671, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(1117, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.0016988m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
