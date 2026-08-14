using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesReverseStreamCompletionTests
{
    private const string ArliAiFixturePath = "Fixtures/chat-completions/raw/arliai-simple-stream.jsonl";

    [Fact]
    public void Completed_response_contains_accumulated_text_output()
    {
        var parts = MapToResponseParts(
            TextEvent("text-start", "msg_1", new AITextStartEventData()),
            TextEvent("text-delta", "msg_1", new AITextDeltaEventData { Delta = "Hello " }),
            TextEvent("text-delta", "msg_1", new AITextDeltaEventData { Delta = "world" }),
            TextEvent("text-end", "msg_1", new AITextEndEventData()),
            FinishEvent("response_1"));

        var completed = Assert.IsType<ResponseCompleted>(parts[^1]);
        var output = Assert.Single(completed.Response.Output);
        var json = JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web);

        Assert.Equal("msg_1", json.GetProperty("id").GetString());
        Assert.Equal("message", json.GetProperty("type").GetString());
        Assert.Equal("completed", json.GetProperty("status").GetString());
        Assert.Equal("assistant", json.GetProperty("role").GetString());
        var content = Assert.Single(json.GetProperty("content").EnumerateArray());
        Assert.Equal("output_text", content.GetProperty("type").GetString());
        Assert.Equal("Hello world", content.GetProperty("text").GetString());
    }

    [Fact]
    public void Completed_response_contains_all_items_in_output_index_order()
    {
        var parts = MapToResponseParts(
            TextEvent("reasoning-start", "reasoning_1", new AIReasoningStartEventData { Signature = "sig_1" }),
            TextEvent("reasoning-delta", "reasoning_1", new AIReasoningDeltaEventData { Delta = "Think" }),
            TextEvent("reasoning-end", "reasoning_1", new AIReasoningEndEventData()),
            TextEvent("text-start", "msg_1", new AITextStartEventData()),
            TextEvent("text-delta", "msg_1", new AITextDeltaEventData { Delta = "Answer" }),
            TextEvent("text-end", "msg_1", new AITextEndEventData()),
            TextEvent("tool-input-start", "call_1", new AIToolInputStartEventData
            {
                ToolName = "lookup",
                ProviderExecuted = true
            }),
            TextEvent("tool-input-available", "call_1", new AIToolInputAvailableEventData
            {
                ToolName = "lookup",
                ProviderExecuted = true,
                Input = JsonSerializer.SerializeToElement(new { query = "weather" })
            }),
            TextEvent("tool-output-available", "call_1", new AIToolOutputAvailableEventData
            {
                ToolName = "lookup",
                ProviderExecuted = true,
                Output = JsonSerializer.SerializeToElement(new { temperature = 20 })
            }),
            FinishEvent("response_2"));

        var completed = Assert.IsType<ResponseCompleted>(parts[^1]);
        var output = completed.Response.Output
            .Select(item => JsonSerializer.SerializeToElement(item, JsonSerializerOptions.Web))
            .ToList();

        Assert.Equal(["reasoning_1", "msg_1", "call_1"], output.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(["reasoning", "message", "custom_tool_call"], output.Select(item => item.GetProperty("type").GetString()));
        Assert.Equal("Think", Assert.Single(output[0].GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("Answer", Assert.Single(output[1].GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.True(output[2].GetProperty("provider_executed").GetBoolean());
        Assert.Equal("weather", output[2].GetProperty("input").GetProperty("query").GetString());
        Assert.Equal(20, output[2].GetProperty("output").GetProperty("temperature").GetInt32());
    }

    [Fact]
    public void Completed_response_clears_reverse_state_before_the_next_stream()
    {
        _ = MapToResponseParts(
            TextEvent("text-start", "old", new AITextStartEventData()),
            TextEvent("text-delta", "old", new AITextDeltaEventData { Delta = "old text" }),
            TextEvent("text-end", "old", new AITextEndEventData()),
            FinishEvent("old_response"));

        var second = MapToResponseParts(
            TextEvent("text-start", "new", new AITextStartEventData()),
            TextEvent("text-delta", "new", new AITextDeltaEventData { Delta = "new text" }),
            TextEvent("text-end", "new", new AITextEndEventData()),
            FinishEvent("new_response"));

        var completed = Assert.IsType<ResponseCompleted>(second[^1]);
        var output = Assert.Single(completed.Response.Output);
        var json = JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web);
        Assert.Equal("new", json.GetProperty("id").GetString());
        Assert.Equal("new text", Assert.Single(json.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ArliAi_chat_completions_fixture_produces_text_in_completed_response_output()
    {
        var updates = FixtureFileLoader.LoadChatCompletionRawFixture(ArliAiFixturePath);
        var provider = new FixtureChatCompletionStreamModelProvider("arliai", updates);
        var request = new AIRequest
        {
            ProviderId = "arliai",
            Model = "Gemma-4-31B-Gutenberg",
            Stream = true,
            Input = new AIInput { Text = "Explain energy efficiency briefly." }
        };

        var responseParts = new List<ResponseStreamPart>();
        await foreach (var streamEvent in provider.StreamUnifiedViaChatCompletionsAsync(request))
            responseParts.Add(streamEvent.ToResponseStreamPart());

        var completed = Assert.IsType<ResponseCompleted>(responseParts[^1]);
        var output = Assert.Single(completed.Response.Output);
        var json = JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web);
        var content = Assert.Single(json.GetProperty("content").EnumerateArray());

        Assert.Equal(
            "Energie-efficiëntie verhoogt de marktwaarde en verkoopbaarheid van het vastgoed en verlaagt de operationele kosten voor de toekomstige bewoners of huurders.",
            content.GetProperty("text").GetString());
        Assert.Equal(63, completed.Response.NormalizedUsage?.InputTokens);
        Assert.Equal(40, completed.Response.NormalizedUsage?.OutputTokens);
        Assert.Equal(103, completed.Response.NormalizedUsage?.TotalTokens);
    }

    private static List<ResponseStreamPart> MapToResponseParts(params AIStreamEvent[] events)
        => events.Select(streamEvent => streamEvent.ToResponseStreamPart()).ToList();

    private static AIStreamEvent TextEvent(string type, string id, object data)
        => new()
        {
            ProviderId = "fixture-provider",
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                Data = data
            }
        };

    private static AIStreamEvent FinishEvent(string id)
        => TextEvent("finish", id, new AIFinishEventData
        {
            FinishReason = "stop",
            Model = "fixture-model",
            CompletedAt = 1_700_000_001,
            InputTokens = 2,
            OutputTokens = 3,
            TotalTokens = 5
        });
}
