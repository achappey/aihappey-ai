using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesUnifiedMapperLifecycleTests
{
    [Fact]
    public async Task Async_mapper_emits_one_anthropic_message_lifecycle_for_many_upstream_chunks()
    {
        var events = CreateTextStream();

        var parts = new List<MessageStreamPart>();
        await foreach (var part in events.ToMessageStreamParts("openai/gpt-test"))
            parts.Add(part);

        Assert.Equal(
        [
            "message_start",
            "content_block_start",
            "content_block_delta",
            "content_block_delta",
            "content_block_delta",
            "content_block_stop",
            "message_delta",
            "message_stop"
        ], parts.Select(part => part.Type).ToArray());

        var start = Assert.Single(parts, part => part.Type == "message_start");
        Assert.Equal("upstream-first", start.Message?.Id);
        Assert.Equal("openai/gpt-test", start.Message?.Model);
        Assert.Equal("assistant", start.Message?.Role);
        Assert.Empty(start.Message?.Content ?? []);
        Assert.Null(start.Message?.StopReason);
        Assert.Null(start.Message?.StopSequence);
        Assert.Equal(0, start.Message?.Usage?.InputTokens);
        Assert.Equal(0, start.Message?.Usage?.OutputTokens);

        Assert.Equal("The quick fox", string.Concat(parts
            .Where(part => part.Type == "content_block_delta")
            .Select(part => part.Delta?.Text)));

        var messageDelta = Assert.Single(parts, part => part.Type == "message_delta");
        Assert.Equal("end_turn", messageDelta.Delta?.StopReason);
        Assert.Null(messageDelta.Delta?.Type);
        Assert.Equal(13, messageDelta.Usage?.InputTokens);
        Assert.Equal(3, messageDelta.Usage?.OutputTokens);
        Assert.Single(parts, part => part.Type == "message_stop");
    }

    [Fact]
    public async Task Synthetic_message_start_serializes_required_null_stop_fields_and_usage_shape()
    {
        MessageStreamPart? start = null;
        await foreach (var part in CreateTextStream().ToMessageStreamParts("openai/gpt-test"))
        {
            if (part.Type == "message_start")
            {
                start = part;
                break;
            }
        }

        var json = JsonSerializer.SerializeToElement(start, MessagesJson.Default);
        var message = json.GetProperty("message");

        Assert.Equal(JsonValueKind.Null, message.GetProperty("stop_reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, message.GetProperty("stop_sequence").ValueKind);
        Assert.Equal(0, message.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(0, message.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    private static async IAsyncEnumerable<AIStreamEvent> CreateTextStream(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return Event("text-start", "chunk-1", new AITextStartEventData(),
            new Dictionary<string, object?> { ["chatcompletions.stream.id"] = "upstream-first" });

        yield return Event("text-delta", "chunk-1", new AITextDeltaEventData { Delta = "The" },
            new Dictionary<string, object?> { ["chatcompletions.stream.id"] = "upstream-second" });
        yield return Event("text-delta", "chunk-1", new AITextDeltaEventData { Delta = " quick" });
        yield return Event("text-delta", "chunk-1", new AITextDeltaEventData { Delta = " fox" });
        yield return Event("text-end", "chunk-1", new AITextEndEventData());
        yield return Event("finish", "upstream-final", new AIFinishEventData
        {
            Model = "openai/gpt-final",
            FinishReason = "stop",
            InputTokens = 13,
            OutputTokens = 3,
            TotalTokens = 16
        });

        await Task.CompletedTask;
    }

    private static AIStreamEvent Event(
        string type,
        string id,
        object data,
        Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = "openai",
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Data = data
            },
            Metadata = metadata
        };
}
