using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Depaza;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Depaza;

public class DepazaProviderCostingTests
{
    private const decimal ExpectedDepazaCoreSampleCost = 0.01569783m;

    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_depaza_catalog_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-3f54707baff02ad9a7de8ee4",
            Created = 1784241123,
            Model = "core",
            Usage = DepazaCoreSampleUsage()
        };

        DepazaProvider.EnrichChatCompletionWithGatewayCostForTests(response, DepazaCorePricing());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedDepazaCoreSampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_depaza_usage_tail_gets_finish_reason_gateway_cost_and_preserves_usage()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "chatcmpl-3f54707baff02ad9a7de8ee4",
            Created = 1784241123,
            Model = "core",
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
            Id = "chatcmpl-3f54707baff02ad9a7de8ee4",
            Created = 1784241123,
            Model = "core",
            Choices = [],
            Usage = DepazaCoreSampleUsage()
        };

        string? lastFinishReason = null;
        DepazaProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        DepazaProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        DepazaProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, DepazaCorePricing());

        var preservedUsage = Assert.IsType<JsonElement>(usageUpdate.Usage);
        Assert.Equal(9453, preservedUsage.GetProperty("prompt_tokens").GetInt32());
        Assert.Single(usageUpdate.Choices);

        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedDepazaCoreSampleCost, gateway?.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("depaza")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(9453, finishData.InputTokens);
        Assert.Equal(89, finishData.OutputTokens);
        Assert.Equal(9542, finishData.TotalTokens);
        Assert.Equal(ExpectedDepazaCoreSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "depaza").Single());

        Assert.Equal(9453, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(89, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(9542, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedDepazaCoreSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages_metadata_shape()
    {
        var unified = new AIResponse
        {
            ProviderId = "depaza",
            Model = "depaza/core",
            Status = "completed",
            Usage = DepazaCoreSampleUsage()
        };

        var enriched = DepazaProvider.EnrichUnifiedResponseWithGatewayCostForTests(unified, DepazaCorePricing());

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedDepazaCoreSampleCost, Assert.IsType<decimal>(gateway["cost"]));

        var response = enriched.ToResponseResult();
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedDepazaCoreSampleCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = enriched.ToMessagesResponse();
        var messagesGateway = messages.Metadata?["gateway"];
        Assert.Equal(ExpectedDepazaCoreSampleCost, messagesGateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_cost_maps_to_responses_messages_and_api_chat_finish_metadata()
    {
        var finishEvent = DepazaProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(
            new AIStreamEvent
            {
                ProviderId = "depaza",
                Event = new AIEventEnvelope
                {
                    Type = "finish",
                    Id = "chatcmpl-3f54707baff02ad9a7de8ee4",
                    Timestamp = DateTimeOffset.Parse("2026-07-16T22:00:00+00:00"),
                    Data = new AIFinishEventData
                    {
                        FinishReason = "stop",
                        Model = "depaza/core",
                        InputTokens = 9453,
                        OutputTokens = 89,
                        TotalTokens = 9542,
                        MessageMetadata = AIFinishMessageMetadata.Create(
                            "depaza/core",
                            DateTimeOffset.Parse("2026-07-16T22:00:00+00:00"),
                            usage: DepazaCoreSampleUsage(),
                            inputTokens: 9453,
                            outputTokens: 89,
                            totalTokens: 9542)
                    }
                }
            },
            DepazaCorePricing());

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(ExpectedDepazaCoreSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var responseCompleted = Assert.IsType<ResponseCompleted>(finishEvent.ToResponseStreamPart());
        var responseUsage = Assert.IsType<JsonElement>(responseCompleted.Response.Usage);
        Assert.Equal(9453, responseUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(89, responseUsage.GetProperty("output_tokens").GetInt32());

        var messageStop = finishEvent
            .ToMessageStreamParts()
            .Single(part => part.Type == "message_stop");
        var messageGateway = messageStop.Metadata?["gateway"];
        Assert.Equal(ExpectedDepazaCoreSampleCost, messageGateway?.GetProperty("cost").GetDecimal());

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "depaza").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(9453, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(89, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(9542, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedDepazaCoreSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static ModelPricing DepazaCorePricing()
        => new()
        {
            Input = 0.00000164m,
            Output = 0.00000219m
        };

    private static JsonElement DepazaCoreSampleUsage()
        => JsonElement("""
        {
            "prompt_tokens": 9453,
            "completion_tokens": 89,
            "total_tokens": 9542
        }
        """);

    private static JsonElement JsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
