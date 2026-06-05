using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.ArceeAI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.ArceeAI;

public class ArceeAIProviderCostingTests
{
    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_arceeai_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "cmpl-arceeai-1",
            Created = 1777285265,
            Model = "arceeai/virtuoso-large",
            Usage = UsageElement("""
            {
                "prompt_tokens": 1000,
                "completion_tokens": 250,
                "total_tokens": 1250
            }
            """)
        };

        ArceeAIProvider.EnrichChatCompletionWithGatewayCostForTests(response, new ModelPricing
        {
            Input = 0.000002m,
            Output = 0.000008m
        });

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.004m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_calculates_gateway_cost_from_arceeai_pricing_with_cached_tokens()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "cmpl-arceeai-2",
            Created = 1777285265,
            Model = "arceeai/spotlight",
            Usage = UsageElement("""
            {
                "prompt_tokens": 1000,
                "completion_tokens": 250,
                "total_tokens": 1250,
                "input_tokens_details": {
                    "cached_tokens": 100
                }
            }
            """)
        };

        ArceeAIProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.0000002m,
            Output = 0.0000004m,
            InputCacheRead = 0.00000002m
        });

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.000302m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_finish_gateway_cost_propagates_to_unified_and_vercel_ui_finish_metadata()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "cmpl-arceeai-3",
            Created = 1777285265,
            Model = "arceeai/virtuoso-large",
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
                "prompt_tokens": 1000,
                "completion_tokens": 250,
                "total_tokens": 1250
            }
            """)
        };

        ArceeAIProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.000002m,
            Output = 0.000008m
        });

        var finishEvent = update
            .ToUnifiedStreamEvents("arceeai")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(0.004m, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "arceeai").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(1000, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(250, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(1250, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.004m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Streaming_usage_only_tail_gets_finish_reason_and_gateway_cost_for_api_chat_finish_metadata()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "cmpl-arceeai-4",
            Created = 1777285265,
            Model = "arceeai/trinity-mini",
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
            Id = "cmpl-arceeai-4",
            Created = 1777285265,
            Model = "arceeai/trinity-mini",
            Usage = UsageElement("""
            {
                "prompt_tokens": 427,
                "completion_tokens": 278,
                "total_tokens": 705
            }
            """)
        };

        string? lastFinishReason = null;
        ArceeAIProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        ArceeAIProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        ArceeAIProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, new ModelPricing
        {
            Input = 0.0000002m,
            Output = 0.0000004m
        });

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("arceeai")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(0.0001966m, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "arceeai").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(427, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(278, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(705, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.0001966m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Unified_finish_without_chat_completion_metadata_gets_gateway_cost_for_api_chat_finish_metadata()
    {
        var finishEvent = new AIStreamEvent
        {
            ProviderId = "arceeai",
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Timestamp = DateTimeOffset.Parse("2026-06-05T12:06:45+00:00"),
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = "arceeai/trinity-mini",
                    InputTokens = 427,
                    OutputTokens = 483,
                    TotalTokens = 910,
                    MessageMetadata = AIFinishMessageMetadata.FromDictionary(new Dictionary<string, object>
                    {
                        ["model"] = "arceeai/trinity-mini",
                        ["usage"] = new Dictionary<string, object?>
                        {
                            ["promptTokens"] = 427,
                            ["completionTokens"] = 483,
                            ["totalTokens"] = 910
                        },
                        ["arceeai"] = new Dictionary<string, object?>
                        {
                            ["usage"] = new Dictionary<string, object?>
                            {
                                ["prompt_tokens"] = 427,
                                ["completion_tokens"] = 483,
                                ["total_tokens"] = 910
                            }
                        }
                    })
                }
            }
        };

        var enriched = ArceeAIProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(finishEvent, new ModelPricing
        {
            Input = 0.0000002m,
            Output = 0.0000004m
        });

        var enrichedFinishData = Assert.IsType<AIFinishEventData>(enriched.Event.Data);
        Assert.Equal(0.0002786m, enrichedFinishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(enriched.Event, "arceeai").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(427, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(483, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(910, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.0002786m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Pricing_lookup_candidates_include_prefixed_and_unprefixed_model_ids()
    {
        var candidates = ArceeAIProvider.GetPricingLookupCandidatesForTests(
                "arceeai/trinity-mini",
                "trinity-large")
            .ToList();

        Assert.Contains("arceeai/trinity-mini", candidates);
        Assert.Contains("trinity-mini", candidates);
        Assert.Contains("trinity-large", candidates);
        Assert.Contains("arceeai/trinity-large", candidates);
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
