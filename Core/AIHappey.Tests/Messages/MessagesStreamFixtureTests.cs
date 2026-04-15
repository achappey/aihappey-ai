using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesStreamFixtureTests
{
    private const string TypedFixturePath = "Fixtures/messages/typed/basic-messages-stream.json";
    private const string RawFixturePath = "Fixtures/messages/raw/basic-messages-stream.jsonl";
    private const string ProviderId = "fixture-provider";
    private const string Model = "claude-haiku-4-5-20251001";
    private const string MessageId = "msg_017Kux9bNH5F1gph8C2FZhP1";

    [Fact]
    public void Typed_messages_fixture_deserializes_expected_stream_parts()
    {
        var parts = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);

        Assert.Equal(
            [
                "message_start",
                "content_block_start",
                "ping",
                "content_block_delta",
                "content_block_delta",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            ],
            parts.Select(part => part.Type).ToList());

        var messageStart = parts[0];
        Assert.Equal(MessageId, messageStart.Message?.Id);
        Assert.Equal(Model, messageStart.Message?.Model);
        Assert.Equal("assistant", messageStart.Message?.Role);
        Assert.Equal(12, messageStart.Message?.Usage?.InputTokens);
        Assert.Equal(1, messageStart.Message?.Usage?.OutputTokens);

        var contentBlockStart = parts[1];
        Assert.Equal(0, contentBlockStart.Index);
        Assert.Equal("text", contentBlockStart.ContentBlock?.Type);
        Assert.Equal(string.Empty, contentBlockStart.ContentBlock?.Text);

        var firstDelta = parts[3];
        Assert.Equal(0, firstDelta.Index);
        Assert.Equal("text_delta", firstDelta.Delta?.Type);
        Assert.Equal("#", firstDelta.Delta?.Text);

        var secondDelta = parts[4];
        Assert.Equal(" yow\n\nHey! 👋 What's up? How can", secondDelta.Delta?.Text);

        var thirdDelta = parts[5];
        Assert.Equal(" I help you?", thirdDelta.Delta?.Text);

        var messageDelta = parts[7];
        Assert.Equal("end_turn", messageDelta.Delta?.StopReason);
        Assert.Equal(12, messageDelta.Usage?.InputTokens);
        Assert.Equal(23, messageDelta.Usage?.OutputTokens);

        var messageStop = parts[8];
        Assert.Null(messageStop.Metadata);
    }

    [Fact]
    public void Raw_messages_fixture_deserializes_expected_stream_parts()
    {
        var parts = FixtureFileLoader.LoadMessageRawFixture(RawFixturePath);

        Assert.Equal(
            [
                "message_start",
                "content_block_start",
                "ping",
                "content_block_delta",
                "content_block_delta",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            ],
            parts.Select(part => part.Type).ToList());

        var messageStart = parts[0];
        Assert.Equal("msg_01FeYqRcJzc1X3ngnRZ7AKRg", messageStart.Message?.Id);
        Assert.Equal("claude-opus-4-6", messageStart.Message?.Model);
        Assert.Equal("assistant", messageStart.Message?.Role);
        Assert.Equal(10, messageStart.Message?.Usage?.InputTokens);
        Assert.Equal(3, messageStart.Message?.Usage?.OutputTokens);

        Assert.Equal("Hello! ", parts[3].Delta?.Text);
        Assert.Equal("👋 Welcome! How can I help you today? Whether you want to chat, ask questions, or work through", parts[4].Delta?.Text);
        Assert.Equal(" something together, I'm here for you. 😊", parts[5].Delta?.Text);

        var messageDelta = parts[7];
        Assert.Equal("end_turn", messageDelta.Delta?.StopReason);
        Assert.Equal(10, messageDelta.Usage?.InputTokens);
        Assert.Equal(42, messageDelta.Usage?.OutputTokens);
    }

    [Fact]
    public void Typed_and_raw_messages_fixtures_produce_the_same_stream_part_types()
    {
        var typed = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);
        var raw = FixtureFileLoader.LoadMessageRawFixture(RawFixturePath);

        Assert.Equal(typed.Select(part => part.Type), raw.Select(part => part.Type));
    }

    [Fact]
    public void Typed_messages_fixture_maps_to_expected_unified_events_and_finish_payload()
    {
        var parts = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);
        var state = new MessagesUnifiedMapper.MessagesStreamMappingState();

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, state))
            .ToList();

        Assert.Equal(
            [
                "data-messages.message_start",
                "data-messages.content_block_start",
                "data-messages.ping",
                "text-start",
                "text-delta",
                "data-messages.content_block_delta",
                "text-delta",
                "data-messages.content_block_delta",
                "text-delta",
                "data-messages.content_block_delta",
                "data-messages.content_block_stop",
                "data-messages.message_delta",
                "text-end",
                "finish",
                "data-messages.message_stop"
            ],
            unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList());

        var textDeltas = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "text-delta")
            .Select(streamEvent => Assert.IsType<AITextDeltaEventData>(streamEvent.Event.Data).Delta)
            .ToList();

        Assert.Equal(
            [
                "#",
                " yow\n\nHey! 👋 What's up? How can",
                " I help you?"
            ],
            textDeltas);
        Assert.Equal("# yow\n\nHey! 👋 What's up? How can I help you?", string.Concat(textDeltas));

        var transientRawEvents = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "data-messages.content_block_delta")
            .Select(streamEvent => Assert.IsType<AIDataEventData>(streamEvent.Event.Data))
            .ToList();

        Assert.Equal(3, transientRawEvents.Count);
        Assert.All(transientRawEvents, rawEvent => Assert.True(rawEvent.Transient));

        var finishEvent = unifiedEvents.Single(streamEvent => streamEvent.Event.Type == "finish");
        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);

        Assert.Equal(Model, finishData.Model);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(12, finishData.InputTokens);
        Assert.Equal(23, finishData.OutputTokens);
        Assert.Equal(35, finishData.TotalTokens);
        Assert.Null(finishData.StopSequence);
        Assert.Null(finishData.MessageMetadata);
        Assert.IsType<long>(finishData.CompletedAt);

        Assert.Equal("message_stop", finishEvent.Metadata?["messages.stream.type"]);

        var lastRawEvent = unifiedEvents.Last();
        var lastRawData = Assert.IsType<AIDataEventData>(lastRawEvent.Event.Data);
        Assert.False(lastRawData.Transient);

        var rawMessageStop = Assert.IsType<JsonElement>(lastRawData.Data);
        Assert.Equal("message_stop", rawMessageStop.GetProperty("type").GetString());
    }

    [Fact]
    public void Unified_events_from_typed_messages_fixture_round_trip_back_to_original_stream_parts()
    {
        var parts = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);
        var mappingState = new MessagesUnifiedMapper.MessagesStreamMappingState();

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, mappingState))
            .ToList();

        var rawBackedEvents = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type.StartsWith("data-messages.", StringComparison.Ordinal))
            .ToList();

        var reverseState = new MessagesUnifiedMapper.MessagesReverseStreamMappingState();
        var roundTripped = rawBackedEvents
            .SelectMany(streamEvent => streamEvent.ToMessageStreamParts(reverseState))
            .ToList();

        Assert.Equal(parts.Select(part => part.Type), roundTripped.Select(part => part.Type));
        Assert.Equal(MessageId, roundTripped[0].Message?.Id);
        Assert.Equal(Model, roundTripped[0].Message?.Model);
        Assert.Equal("#", roundTripped[3].Delta?.Text);
        Assert.Equal(" yow\n\nHey! 👋 What's up? How can", roundTripped[4].Delta?.Text);
        Assert.Equal(" I help you?", roundTripped[5].Delta?.Text);
        Assert.Equal("end_turn", roundTripped[7].Delta?.StopReason);
        Assert.Equal(23, roundTripped[7].Usage?.OutputTokens);
    }

    [Fact]
    public void Synthetic_messages_unified_events_expand_into_expected_message_stream_parts()
    {
        var reverseState = new MessagesUnifiedMapper.MessagesReverseStreamMappingState();
        var sharedMetadata = new Dictionary<string, object?>
        {
            ["messages.response.id"] = "msg_synthetic_fixture",
            ["messages.response.model"] = Model,
            ["messages.response.role"] = "assistant"
        };

        var syntheticEvents = new List<AIStreamEvent>
        {
            new()
            {
                ProviderId = ProviderId,
                Event = new AIEventEnvelope
                {
                    Type = "text-delta",
                    Id = "msg_synthetic_fixture:0:text",
                    Data = new AITextDeltaEventData
                    {
                        Delta = "Hello from synthetic unified event"
                    }
                },
                Metadata = sharedMetadata
            },
            new()
            {
                ProviderId = ProviderId,
                Event = new AIEventEnvelope
                {
                    Type = "finish",
                    Id = "msg_synthetic_fixture",
                    Data = new AIFinishEventData
                    {
                        Model = Model,
                        FinishReason = "stop",
                        InputTokens = 9,
                        OutputTokens = 16,
                        TotalTokens = 25
                    }
                },
                Metadata = sharedMetadata
            }
        };

        var parts = syntheticEvents
            .SelectMany(streamEvent => streamEvent.ToMessageStreamParts(reverseState))
            .ToList();

        Assert.Equal(
            [
                "message_start",
                "content_block_start",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            ],
            parts.Select(part => part.Type).ToList());

        Assert.Equal("msg_synthetic_fixture", parts[0].Message?.Id);
        Assert.Equal(Model, parts[0].Message?.Model);
        Assert.Equal("assistant", parts[0].Message?.Role);
        Assert.Equal("text", parts[1].ContentBlock?.Type);
        Assert.Equal("Hello from synthetic unified event", parts[2].Delta?.Text);
        Assert.Equal("text_delta", parts[2].Delta?.Type);
        Assert.Equal("stop_sequence", parts[4].Delta?.StopReason);
        Assert.Equal(9, parts[4].Usage?.InputTokens);
        Assert.Equal(16, parts[4].Usage?.OutputTokens);
        Assert.Null(parts[5].Metadata);
    }
}
