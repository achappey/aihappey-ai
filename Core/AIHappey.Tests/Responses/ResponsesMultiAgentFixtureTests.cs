using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesMultiAgentFixtureTests
{
    private const string FixturePath = "Fixtures/responses/raw/openai-with-subagents-stream.jsonl";
    private const string ProviderId = "openai";

    [Fact]
    public void Multi_agent_stream_emits_only_the_last_assistant_message()
    {
        var parts = FixtureFileLoader.LoadResponseRawFixture(FixturePath);
        var mappingState = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        var userMessageIds = parts
            .OfType<ResponseOutputItemDone>()
            .Where(part => part.Item.Type == "message" && part.Item.Role == "user")
            .Select(part => part.Item.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var expectedFinalId = parts
            .OfType<ResponseCompleted>()
            .Single()
            .Response.Output
            .Select(item => JsonSerializer.SerializeToElement(item, ResponseJson.Default))
            .Where(item => item.GetProperty("type").GetString() == "message"
                           && item.GetProperty("role").GetString() == "assistant")
            .Last(item => item.GetProperty("phase").GetString() == "final_answer")
            .GetProperty("id")
            .GetString();

        var visibleTextEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId, mappingState))
            .Where(streamEvent => streamEvent.Event.Type is "text-start" or "text-delta" or "text-end")
            .ToList();

        Assert.NotEmpty(userMessageIds);
        Assert.DoesNotContain(visibleTextEvents, streamEvent => userMessageIds.Contains(streamEvent.Event.Id));
        Assert.Equal([expectedFinalId], visibleTextEvents
            .Where(streamEvent => streamEvent.Event.Type == "text-start")
            .Select(streamEvent => streamEvent.Event.Id)
            .ToList());
        Assert.Equal([expectedFinalId], visibleTextEvents
            .Where(streamEvent => streamEvent.Event.Type == "text-end")
            .Select(streamEvent => streamEvent.Event.Id)
            .ToList());
    }

    [Fact]
    public void Subagent_messages_are_packed_into_finish_metadata_for_native_replay()
    {
        var mappingState = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        var finish = FixtureFileLoader.LoadResponseRawFixture(FixturePath)
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId, mappingState))
            .Single(streamEvent => streamEvent.Event.Type == "finish");
        var data = Assert.IsType<AIFinishEventData>(finish.Event.Data);
        var metadata = data.MessageMetadata?.ToDictionary();
        var provider = Assert.IsType<JsonElement>(Assert.Contains(ProviderId, metadata ?? []));
        var replayItems = provider.GetProperty("responses.opaque_replay_items");

        Assert.Contains(replayItems.EnumerateArray(), entry =>
            entry.GetProperty("item").GetProperty("type").GetString() == "agent_message");
        Assert.Contains(replayItems.EnumerateArray(), entry =>
            entry.GetProperty("item").GetProperty("type").GetString() == "message"
            && entry.GetProperty("item").GetProperty("agent").GetProperty("agent_name").GetString() != "/root");
    }

    [Fact]
    public void Multi_agent_tool_ui_history_rebuilds_native_call_with_arguments_and_output()
    {
        var mappingState = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        var events = FixtureFileLoader.LoadResponseRawFixture(FixturePath)
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId, mappingState))
            .ToList();

        var inputEvent = events.First(streamEvent =>
            streamEvent.Event.Type == "tool-input-available"
            && streamEvent.Event.Data is AIToolInputAvailableEventData data
            && data.ToolName == "multi_agent");
        var outputEvent = events.First(streamEvent =>
            streamEvent.Event.Type == "tool-output-available"
            && streamEvent.Event.Id == inputEvent.Event.Id
            && streamEvent.Event.Data is AIToolOutputAvailableEventData data
            && data.ToolName == "multi_agent");

        var inputPart = Assert.IsType<ToolCallPart>(Assert.Single(inputEvent.Event.ToUIMessagePart(ProviderId)));
        var outputPart = Assert.IsType<ToolOutputAvailablePart>(Assert.Single(outputEvent.Event.ToUIMessagePart(ProviderId)));
        var invocation = new ToolInvocationPart
        {
            Type = "tool-multi_agent",
            ToolCallId = inputPart.ToolCallId,
            Title = inputPart.Title,
            Input = inputPart.Input,
            Output = outputPart.Output,
            State = "output-available",
            ProviderExecuted = true,
            CallProviderMetadata = inputPart.ProviderMetadata,
            ResultProviderMetadata = outputPart.ProviderMetadata
        };
        var unifiedInputItem = new UIMessage
        {
            Id = Guid.NewGuid().ToString(),
            Role = Role.assistant,
            Parts = [invocation]
        }.ToUnifiedInputItem();

        var nativeRequest = new AIRequest
        {
            ProviderId = ProviderId,
            Model = "gpt-test",
            Input = new AIInput { Items = [unifiedInputItem] }
        }.ToResponseRequest(ProviderId);
        var nativeItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(nativeRequest.Input?.Items);

        var call = Assert.IsType<ResponseMultiAgentCallItem>(nativeItems[0]);
        var output = Assert.IsType<ResponseMultiAgentCallOutputItem>(nativeItems[1]);
        Assert.False(string.IsNullOrWhiteSpace(call.Arguments));
        Assert.Equal(call.CallId, output.CallId);

        var serialized = JsonSerializer.Serialize(nativeRequest, ResponseJson.Default);
        using var document = JsonDocument.Parse(serialized);
        var serializedCall = document.RootElement.GetProperty("input")[0];
        Assert.Equal("multi_agent_call", serializedCall.GetProperty("type").GetString());
        Assert.True(serializedCall.TryGetProperty("arguments", out var arguments));
        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(arguments.GetString()));
    }
}
