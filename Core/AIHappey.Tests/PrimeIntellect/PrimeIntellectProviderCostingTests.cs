using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.PrimeIntellect;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.PrimeIntellect;

public class PrimeIntellectProviderCostingTests
{
    private const decimal ExpectedTerraSampleCost = 0.000466m;

    [Fact]
    public void ChatCompletion_enrichment_calculates_gateway_cost_from_primeintellect_model_listing_pricing()
    {
        var response = new ChatCompletion
        {
            Id = "a1c7be28cb72a5c5-AMS",
            Created = 1784275540,
            Model = "openai/gpt-5.6-terra",
            Usage = TerraSampleUsage()
        };

        PrimeIntellectProvider.EnrichChatCompletionWithGatewayCostForTests(response, TerraPricing());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedTerraSampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_primeintellect_usage_tail_gets_finish_reason_and_gateway_cost()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "a1c7be28cb72a5c5-AMS",
            Created = 1784275539,
            Model = "openai/gpt-5.6-terra",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { content = "", role = "assistant" },
                    finish_reason = "stop",
                    native_finish_reason = "completed"
                }
            ]
        };

        var usageUpdate = new ChatCompletionUpdate
        {
            Id = "a1c7be28cb72a5c5-AMS",
            Created = 1784275540,
            Model = "openai/gpt-5.6-terra",
            Usage = TerraSampleUsage()
        };

        string? lastFinishReason = null;
        PrimeIntellectProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        PrimeIntellectProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        PrimeIntellectProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate, TerraPricing());

        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedTerraSampleCost, gateway?.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("primeintellect")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(439, finishData.InputTokens);
        Assert.Equal(9, finishData.OutputTokens);
        Assert.Equal(448, finishData.TotalTokens);
        Assert.Equal(ExpectedTerraSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "primeintellect").Single());

        Assert.Equal(439, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(9, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(448, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedTerraSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages_metadata_shape()
    {
        var unified = new AIResponse
        {
            ProviderId = "primeintellect",
            Model = "primeintellect/openai/gpt-5.6-terra",
            Status = "completed",
            Usage = TerraSampleUsage()
        };

        var enriched = PrimeIntellectProvider.EnrichUnifiedResponseWithGatewayCostForTests(unified, TerraPricing());

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedTerraSampleCost, Assert.IsType<decimal>(gateway["cost"]));

        var response = enriched.ToResponseResult();
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedTerraSampleCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = enriched.ToMessagesResponse();
        var messagesGateway = messages.Metadata?["gateway"];
        Assert.Equal(ExpectedTerraSampleCost, messagesGateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_cost_maps_to_responses_messages_and_api_chat_finish_metadata()
    {
        var finishEvent = PrimeIntellectProvider.EnrichUnifiedFinishEventWithGatewayCostForTests(
            new AIStreamEvent
            {
                ProviderId = "primeintellect",
                Event = new AIEventEnvelope
                {
                    Type = "finish",
                    Id = "a1c7be28cb72a5c5-AMS",
                    Timestamp = DateTimeOffset.Parse("2026-07-17T08:00:00+00:00"),
                    Data = new AIFinishEventData
                    {
                        FinishReason = "stop",
                        Model = "primeintellect/openai/gpt-5.6-terra",
                        InputTokens = 439,
                        OutputTokens = 9,
                        TotalTokens = 448,
                        MessageMetadata = AIFinishMessageMetadata.Create(
                            "primeintellect/openai/gpt-5.6-terra",
                            DateTimeOffset.Parse("2026-07-17T08:00:00+00:00"),
                            usage: TerraSampleUsage(),
                            inputTokens: 439,
                            outputTokens: 9,
                            totalTokens: 448)
                    }
                }
            },
            TerraPricing());

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(ExpectedTerraSampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var responseCompleted = Assert.IsType<ResponseCompleted>(finishEvent.ToResponseStreamPart());
        var responseUsage = Assert.IsType<JsonElement>(responseCompleted.Response.Usage);
        Assert.Equal(439, responseUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(9, responseUsage.GetProperty("output_tokens").GetInt32());

        var messageStop = finishEvent
            .ToMessageStreamParts()
            .Single(part => part.Type == "message_stop");
        var messageGateway = messageStop.Metadata?["gateway"];
        Assert.Equal(ExpectedTerraSampleCost, messageGateway?.GetProperty("cost").GetDecimal());

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "primeintellect").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(439, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(9, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(448, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedTerraSampleCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static ModelPricing TerraPricing()
        => new()
        {
            Input = 0.000001m,
            Output = 0.000003m
        };

    private static JsonElement TerraSampleUsage()
        => JsonElement("""
        {
            "prompt_tokens": 439,
            "completion_tokens": 9,
            "total_tokens": 448,
            "prompt_tokens_details": {
                "cached_tokens": 0,
                "cache_write_tokens": 0,
                "audio_tokens": 0,
                "video_tokens": 0
            },
            "completion_tokens_details": {
                "reasoning_tokens": 0,
                "image_tokens": 0,
                "audio_tokens": 0
            }
        }
        """);

    private static JsonElement JsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
