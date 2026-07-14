using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Providers.BeastLabAI;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.BeastLabAI;

public class BeastLabAIProviderCostingTests
{
    private const decimal ExpectedCost = 0.043673121271m;

    [Fact]
    public void ChatCompletionUpdate_top_level_cost_is_attached_to_later_usage_finish_shape()
    {
        decimal? latestStreamCost = null;

        var costUpdate = BeastLabAIStreamUpdate("""
        {
            "id":"chatcmpl-68b4b7f00764",
            "created":1784013676,
            "model":"beast-mini",
            "object":"chat.completion.chunk",
            "choices":[{"finish_reason":"stop","index":0,"delta":{}}],
            "cost":0.043673121271
        }
        """);

        BeastLabAIProvider.EnrichChatCompletionUpdateWithBeastLabAIStreamCostForTests(costUpdate, ref latestStreamCost);

        var costGateway = costUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedCost, costGateway?.GetProperty("cost").GetDecimal());

        var usageUpdate = BeastLabAIStreamUpdate("""
        {
            "id":"chatcmpl-68b4b7f00764",
            "created":1784013676,
            "model":"beast-mini",
            "object":"chat.completion.chunk",
            "choices":[{"index":0,"delta":{}}],
            "usage":{
                "completion_tokens":2654,
                "prompt_tokens":358,
                "total_tokens":3012,
                "completion_tokens_details":{"reasoning_tokens":37},
                "prompt_tokens_details":{"cached_tokens":39}
            }
        }
        """);

        BeastLabAIProvider.EnrichChatCompletionUpdateWithBeastLabAIStreamCostForTests(usageUpdate, ref latestStreamCost);

        var usageGateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedCost, usageGateway?.GetProperty("cost").GetDecimal());

        var usage = Assert.IsType<JsonElement>(usageUpdate.Usage);
        Assert.Equal(ExpectedCost, usage.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Unified_finish_cost_maps_to_responses_messages_and_api_chat_finish_metadata()
    {
        var update = BeastLabAIStreamUpdate("""
        {
            "id":"chatcmpl-68b4b7f00764",
            "created":1784013676,
            "model":"beast-mini",
            "object":"chat.completion.chunk",
            "choices":[{"finish_reason":"stop","index":0,"delta":{}}],
            "usage":{
                "completion_tokens":2654,
                "prompt_tokens":358,
                "total_tokens":3012,
                "cost":0.043673121271
            },
            "metadata":{
                "gateway":{"cost":0.043673121271}
            }
        }
        """);

        var finishEvent = CreateUnifiedFinishEvent(update);

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(358, finishData.InputTokens);
        Assert.Equal(2654, finishData.OutputTokens);
        Assert.Equal(3012, finishData.TotalTokens);
        Assert.Equal(ExpectedCost, finishData.MessageMetadata?.Gateway?.Cost);

        var responseCompleted = Assert.IsType<ResponseCompleted>(finishEvent.ToResponseStreamPart());
        var responseUsage = Assert.IsType<JsonElement>(responseCompleted.Response.Usage);
        Assert.Equal(ExpectedCost, responseUsage.GetProperty("cost").GetDecimal());

        var messageStop = finishEvent
            .ToMessageStreamParts()
            .Single(part => part.Type == "message_stop");
        var messageGateway = messageStop.Metadata?["gateway"];
        Assert.Equal(ExpectedCost, messageGateway?.GetProperty("cost").GetDecimal());

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "beastlabai").Single());
        Assert.Equal(ExpectedCost, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Missing_cost_leaves_updates_unchanged_and_does_not_fail()
    {
        decimal? latestStreamCost = null;
        var update = BeastLabAIStreamUpdate("""
        {
            "id":"chatcmpl-68b4b7f00764",
            "created":1784013676,
            "model":"beast-mini",
            "object":"chat.completion.chunk",
            "choices":[{"finish_reason":"stop","index":0,"delta":{}}]
        }
        """);

        var enriched = BeastLabAIProvider.EnrichChatCompletionUpdateWithBeastLabAIStreamCostForTests(update, ref latestStreamCost);

        Assert.Null(latestStreamCost);
        Assert.Empty(enriched.AdditionalProperties ?? []);
    }

    [Fact]
    public void Unified_response_cost_maps_to_responses_and_messages_metadata_shape()
    {
        var unified = new AIResponse
        {
            ProviderId = "beastlabai",
            Model = "beast-mini",
            Status = "completed",
            Usage = UsageElement("""
            {
                "prompt_tokens":358,
                "completion_tokens":2654,
                "total_tokens":3012,
                "cost":0.043673121271
            }
            """)
        };

        var enriched = BeastLabAIProvider.EnrichUnifiedResponseWithBeastLabAICostForTests(unified);

        var gateway = Assert.IsType<Dictionary<string, object?>>(enriched.Metadata?["gateway"]);
        Assert.Equal(ExpectedCost, Assert.IsType<decimal>(gateway["cost"]));

        var response = enriched.ToResponseResult();
        var responseGateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(ExpectedCost, Assert.IsType<decimal>(responseGateway["cost"]));

        var messages = enriched.ToMessagesResponse();
        var messagesGateway = messages.Metadata?["gateway"];
        Assert.Equal(ExpectedCost, messagesGateway?.GetProperty("cost").GetDecimal());
    }

    private static ChatCompletionUpdate BeastLabAIStreamUpdate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ChatCompletionUpdate
        {
            Id = root.GetProperty("id").GetString()!,
            Object = root.GetProperty("object").GetString()!,
            Created = root.GetProperty("created").GetInt64(),
            Model = root.GetProperty("model").GetString()!,
            Choices = root.GetProperty("choices").EnumerateArray().Select(choice => choice.Clone()).Cast<object>().ToList(),
            Usage = root.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
            AdditionalProperties = root.EnumerateObject()
                .Where(property => property.Name is not "id" and not "object" and not "created" and not "model" and not "choices" and not "usage")
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
    }

    private static AIStreamEvent CreateUnifiedFinishEvent(ChatCompletionUpdate update)
    {
        var usage = Assert.IsType<JsonElement>(update.Usage);
        var metadata = update.AdditionalProperties is not null
                       && update.AdditionalProperties.TryGetValue("metadata", out var metadataElement)
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(metadataElement.GetRawText(), JsonSerializerOptions.Web) ?? []
            : [];

        metadata["usage"] = usage;
        metadata["inputTokens"] = usage.GetProperty("prompt_tokens").GetInt32();
        metadata["outputTokens"] = usage.GetProperty("completion_tokens").GetInt32();
        metadata["totalTokens"] = usage.GetProperty("total_tokens").GetInt32();

        return new AIStreamEvent
        {
            ProviderId = "beastlabai",
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = update.Id,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(update.Created),
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = update.Model,
                    CompletedAt = update.Created,
                    InputTokens = usage.GetProperty("prompt_tokens").GetInt32(),
                    OutputTokens = usage.GetProperty("completion_tokens").GetInt32(),
                    TotalTokens = usage.GetProperty("total_tokens").GetInt32(),
                    MessageMetadata = AIFinishMessageMetadata.FromDictionary(metadata)
                }
            }
        };
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
