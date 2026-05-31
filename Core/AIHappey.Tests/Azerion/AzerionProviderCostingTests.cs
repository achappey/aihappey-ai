using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Azerion;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Azerion;

public class AzerionProviderCostingTests
{
    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "gen-1",
            Created = 1777285265,
            Model = "azerion/alibaba-qwen-3",
            Usage = UsageElement("""
            {
                "prompt_tokens": 444,
                "completion_tokens": 13,
                "total_tokens": 457
            }
            """)
        };

        AzerionProvider.EnrichChatCompletionWithGatewayCostForTests(response, new ModelPricing
        {
            Input = 0.00000023m,
            Output = 0.0000023m
        });

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00013202m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_calculates_gateway_cost_from_pricing_with_cached_tokens()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "gen-2",
            Created = 1777285265,
            Model = "azerion/alibaba-qwen-3",
            Usage = UsageElement("""
            {
                "prompt_tokens": 444,
                "completion_tokens": 13,
                "total_tokens": 457,
                "prompt_tokens_details": {
                    "cached_tokens": 10
                }
            }
            """)
        };

        AzerionProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.0000002m,
            Output = 0.000001m,
            InputCacheRead = 0.00000005m
        });

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.0001018m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_finish_gateway_cost_propagates_to_unified_and_vercel_ui_finish_metadata()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "gen-3",
            Created = 1777285265,
            Model = "azerion/alibaba-qwen-3",
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
                "prompt_tokens": 444,
                "completion_tokens": 13,
                "total_tokens": 457
            }
            """)
        };

        update.AdditionalProperties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["metadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["gateway"] = new Dictionary<string, object?>
                {
                    ["cost"] = 0.00013202m
                }
            }, JsonSerializerOptions.Web)
        };

        var finishEvent = update
            .ToUnifiedStreamEvents("azerion")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(0.00013202m, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "azerion").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(444, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(13, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(457, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.00013202m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
