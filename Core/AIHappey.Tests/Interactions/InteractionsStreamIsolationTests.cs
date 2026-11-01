using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsStreamIsolationTests
{
    [Fact]
    public void Interleaved_streams_preserve_arguments_names_ids_and_provider_identity()
    {
        var first = CreateMapper();
        var second = CreateMapper();
        Start(first, "first_tool", "first");
        Start(second, "second_tool", "second");

        AssertCall(first(new InteractionStepStopEvent { Index = 0 }), "first_tool", "first");
        AssertCall(second(new InteractionStepStopEvent { Index = 0 }), "second_tool", "second");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Another_streams_terminal_event_cannot_reset_an_active_call(bool error)
    {
        var first = CreateMapper();
        var second = CreateMapper();
        Start(first, "first_tool", "first");
        Start(second, "second_tool", "second");
        second(error ? new InteractionErrorEvent() : new InteractionCompletedEvent());

        AssertCall(first(new InteractionStepStopEvent { Index = 0 }), "first_tool", "first");
    }

    [Fact]
    public void Starting_a_new_stream_cannot_erase_an_active_calls_arguments()
    {
        var first = CreateMapper();
        Start(first, "first_tool", "first");
        var second = CreateMapper();
        second(new InteractionCreatedEvent());

        AssertCall(first(new InteractionStepStopEvent { Index = 0 }), "first_tool", "first");
    }

    [Fact]
    public void Discarding_an_unfinished_stream_cannot_contaminate_another_stream()
    {
        var abandoned = CreateMapper();
        Start(abandoned, "abandoned_tool", "private");
        var active = CreateMapper();
        Start(active, "active_tool", "public");
        AssertCall(active(new InteractionStepStopEvent { Index = 0 }), "active_tool", "public");

        AssertCall(abandoned(new InteractionStepStopEvent { Index = 0 }), "abandoned_tool", "private");
    }

    [Fact]
    public void Disposing_a_partially_enumerated_sequence_does_not_clear_another_stream()
    {
        var active = CreateMapper();
        Start(active, "active_tool", "active");
        var disposed = false;
        using (var enumerator = UnfinishedSequence().ToUnifiedStreamEvents("google").GetEnumerator())
            Assert.True(enumerator.MoveNext());

        Assert.True(disposed);
        AssertCall(active(new InteractionStepStopEvent { Index = 0 }), "active_tool", "active");

        IEnumerable<InteractionStreamEventPart> UnfinishedSequence()
        {
            try
            {
                yield return new InteractionCreatedEvent();
                yield return new InteractionStepStartEvent
                {
                    Index = 0,
                    Step = new InteractionFunctionCallStep { Id = "same-call-id", Name = "abandoned_tool" }
                };
                throw new InvalidOperationException("Early disposal must not drain the source.");
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public async Task Canceling_a_stream_disposes_its_source_without_clearing_another_stream()
    {
        var active = CreateMapper();
        Start(active, "active_tool", "active");
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        await using var enumerator = UnfinishedSequence().ToUnifiedStreamEvents("google", cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await enumerator.MoveNextAsync()) { }
        });

        Assert.True(disposed);
        AssertCall(active(new InteractionStepStopEvent { Index = 0 }), "active_tool", "active");

        async IAsyncEnumerable<InteractionStreamEventPart> UnfinishedSequence(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                yield return new InteractionCreatedEvent();
                yield return new InteractionStepStartEvent
                {
                    Index = 0,
                    Step = new InteractionFunctionCallStep { Id = "same-call-id", Name = "canceled_tool" }
                };
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public void Explicit_state_disposal_does_not_clear_another_stream()
    {
        var active = CreateMapper();
        Start(active, "active_tool", "active");
        using (var state = new InteractionsUnifiedMapper.InteractionStreamMappingState())
            Start(part => part.ToUnifiedStreamEvent("google", state).ToList(), "disposed_tool", "disposed");

        AssertCall(active(new InteractionStepStopEvent { Index = 0 }), "active_tool", "active");
    }

    [Fact]
    public void Interleaved_media_streams_preserve_their_own_payload_and_provider_metadata()
    {
        var first = CreateMapper();
        var second = CreateMapper();
        first(new InteractionCreatedEvent());
        first(new InteractionStepStartEvent
        {
            Index = 0,
            Step = new InteractionModelOutputStep { Content = [new InteractionVideoContent { MimeType = "video/mp4", Data = "Zmlyc3Q=" }] }
        });
        second(new InteractionCreatedEvent());
        second(new InteractionStepStartEvent
        {
            Index = 0,
            Step = new InteractionModelOutputStep { Content = [new InteractionVideoContent { MimeType = "video/webm", Data = "c2Vjb25k" }] }
        });
        var firstOutput = Assert.Single(first(new InteractionStepStopEvent { Index = 0 }), e => e.Event.Type == "tool-output-available");
        var secondOutput = Assert.Single(second(new InteractionStepStopEvent { Index = 0 }), e => e.Event.Type == "tool-output-available");

        var firstJson = JsonSerializer.Serialize(Assert.IsType<AIToolOutputAvailableEventData>(firstOutput.Event.Data));
        var secondJson = JsonSerializer.Serialize(Assert.IsType<AIToolOutputAvailableEventData>(secondOutput.Event.Data));
        Assert.Contains("Zmlyc3Q=", firstJson);
        Assert.Contains("video/mp4", firstJson);
        Assert.DoesNotContain("c2Vjb25k", firstJson);
        Assert.Contains("c2Vjb25k", secondJson);
        Assert.Contains("video/webm", secondJson);
        Assert.DoesNotContain("Zmlyc3Q=", secondJson);
        Assert.Equal("google", firstOutput.ProviderId);
        Assert.Equal("google", secondOutput.ProviderId);
    }

    [Fact]
    public void Interleaved_reasoning_streams_preserve_their_own_signatures()
    {
        var first = CreateMapper();
        var second = CreateMapper();
        first(new InteractionCreatedEvent());
        first(new InteractionStepStartEvent
        {
            Index = 0,
            Step = new InteractionThoughtStep { Signature = "first-private-signature" }
        });
        second(new InteractionCreatedEvent());
        second(new InteractionStepStartEvent
        {
            Index = 0,
            Step = new InteractionThoughtStep { Signature = "second-private-signature" }
        });
        var firstEnd = Assert.IsType<AIReasoningEndEventData>(
            Assert.Single(first(new InteractionStepStopEvent { Index = 0 }), e => e.Event.Type == "reasoning-end").Event.Data);
        var secondEnd = Assert.IsType<AIReasoningEndEventData>(
            Assert.Single(second(new InteractionStepStopEvent { Index = 0 }), e => e.Event.Type == "reasoning-end").Event.Data);

        Assert.Equal("first-private-signature", firstEnd.Signature);
        Assert.Equal("first-private-signature", firstEnd.ProviderMetadata!["google"]["signature"]);
        Assert.Equal("second-private-signature", secondEnd.Signature);
        Assert.Equal("second-private-signature", secondEnd.ProviderMetadata!["google"]["signature"]);
    }

    private static Func<InteractionStreamEventPart, List<AIStreamEvent>> CreateMapper()
    {
        var state = new InteractionsUnifiedMapper.InteractionStreamMappingState();
        return part => part.ToUnifiedStreamEvent("google", state).ToList();
    }

    private static void Start(Func<InteractionStreamEventPart, List<AIStreamEvent>> map, string name, string value)
    {
        map(new InteractionCreatedEvent());
        map(new InteractionStepStartEvent
        {
            Index = 0,
            Step = new InteractionFunctionCallStep { Id = "same-call-id", Name = name }
        });
        map(new InteractionStepDeltaEvent
        {
            Index = 0,
            Delta = new InteractionArgumentsDeltaData
            {
                Arguments = $"{{\"value\":\"{value}\"}}"
            }
        });
    }

    private static void AssertCall(List<AIStreamEvent> events, string name, string value)
    {
        var streamEvent = Assert.Single(events, e => e.Event.Type == "tool-input-available");
        Assert.Equal("google", streamEvent.ProviderId);
        Assert.Equal("same-call-id", streamEvent.Event.Id);
        var data = Assert.IsType<AIToolInputAvailableEventData>(streamEvent.Event.Data);
        Assert.Equal(name, data.ToolName);
        Assert.Equal(value, JsonSerializer.SerializeToElement(data.Input).GetProperty("value").GetString());
    }
}
