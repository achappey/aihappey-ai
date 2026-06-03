using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AI21;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.AI21;

public class AI21ProviderCostingTests
{
    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_ai21_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "cmpl-ai21-1",
            Created = 1777285265,
            Model = "ai21/jamba-large",
            Usage = UsageElement("""
            {
                "prompt_tokens": 1000,
                "completion_tokens": 250,
                "total_tokens": 1250
            }
            """)
        };

        AI21Provider.EnrichChatCompletionWithGatewayCostForTests(response, new ModelPricing
        {
            Input = 0.000002m,
            Output = 0.000008m
        });

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.004m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_calculates_gateway_cost_from_ai21_pricing()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "cmpl-ai21-2",
            Created = 1777285265,
            Model = "ai21/jamba-mini",
            Usage = UsageElement("""
            {
                "prompt_tokens": 1000,
                "completion_tokens": 250,
                "total_tokens": 1250
            }
            """)
        };

        AI21Provider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.0000002m,
            Output = 0.0000004m
        });

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.0003m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_finish_gateway_cost_propagates_to_unified_and_vercel_ui_finish_metadata()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "cmpl-ai21-3",
            Created = 1777285265,
            Model = "ai21/jamba-large",
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

        AI21Provider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.000002m,
            Output = 0.000008m
        });

        var finishEvent = update
            .ToUnifiedStreamEvents("ai21")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(0.004m, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "ai21").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(1000, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(250, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(1250, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.004m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
