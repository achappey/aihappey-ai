using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class ApiChatStreamFixtureTests
{
    private const string TypedFixturePath = "Fixtures/api-chat/typed/basic-ui-stream.json";
    private const string RawFixturePath = "Fixtures/api-chat/raw/basic-ui-stream.jsonl";

    [Fact]
    public void Typed_and_raw_api_chat_fixtures_produce_the_same_ui_part_types()
    {
        var typed = FixtureFileLoader.LoadUiTypedFixture(TypedFixturePath);
        var raw = FixtureFileLoader.LoadUiRawFixture(RawFixturePath);

        Assert.Equal(typed.Select(part => part.Type), raw.Select(part => part.Type));

        var typedFinish = Assert.IsType<FinishUIPart>(typed.Last());
        var rawFinish = Assert.IsType<FinishUIPart>(raw.Last());

        Assert.Equal("fixture-provider/gpt-fixture", typedFinish.MessageMetadata?.Model);
        Assert.Equal(typedFinish.MessageMetadata?.Model, rawFinish.MessageMetadata?.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-04-15T14:11:18.3896779Z"), typedFinish.MessageMetadata?.Timestamp);
        Assert.Equal(typedFinish.MessageMetadata?.Timestamp, rawFinish.MessageMetadata?.Timestamp);
        Assert.Equal(7, typedFinish.MessageMetadata?.InputTokens);
        Assert.Null(typedFinish.MessageMetadata?.OutputTokens);
        Assert.Equal(12, typedFinish.MessageMetadata?.TotalTokens);
        Assert.Equal(0.3f, typedFinish.MessageMetadata?.Temperature);
        Assert.Equal(0.00011590m, typedFinish.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Api_chat_fixture_round_trips_through_the_unified_event_mapper()
    {
        var parts = FixtureFileLoader.LoadUiTypedFixture(TypedFixturePath);

        var envelopes = parts.Select(VercelUnifiedMapper.ToUnifiedEvent).ToList();
        var envelopeTypes = envelopes.Select(envelope => envelope.Type).ToList();

        FixtureAssertions.AssertContainsSubsequence(
            envelopeTypes,
            "vercel.ui.text-start",
            "vercel.ui.text-delta",
            "vercel.ui.text-end",
            "vercel.ui.source-url",
            "vercel.ui.finish");

        var stableEnvelopes = envelopes
            .Where(envelope => envelope.Type is "vercel.ui.text-start"
                or "vercel.ui.text-delta"
                or "vercel.ui.text-end"
                or "vercel.ui.source-url")
            .ToList();

        var roundTripped = stableEnvelopes
            .SelectMany(envelope => envelope.ToUIMessagePart("fixture-provider"))
            .ToList();

        Assert.Equal(
            parts.Take(4).Select(part => part.Type),
            roundTripped.Select(part => part.Type));

        var textDelta = Assert.IsType<TextDeltaUIMessageStreamPart>(roundTripped[1]);
        Assert.Equal("Hello from api/chat", textDelta.Delta);

        var finishEnvelope = envelopes.Single(envelope => envelope.Type == "vercel.ui.finish");
        var finishPart = Assert.IsType<FinishUIPart>(parts.Last());
        Assert.Equal("finish", finishEnvelope.Data is null ? null : "finish");
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal("fixture-provider/gpt-fixture", finishPart.MessageMetadata?.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-04-15T14:11:18.3896779Z"), finishPart.MessageMetadata?.Timestamp);
        Assert.Equal(7, finishPart.MessageMetadata?.InputTokens);
        Assert.Null(finishPart.MessageMetadata?.OutputTokens);
        Assert.Equal(12, finishPart.MessageMetadata?.TotalTokens);
        Assert.Equal(0.3f, finishPart.MessageMetadata?.Temperature);
        Assert.Equal(0.00011590m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public void Finish_event_backfills_token_counts_from_finish_event_data_when_message_metadata_only_has_gateway_cost()
    {
        var timestamp = DateTimeOffset.Parse("2026-04-15T15:34:57.263811+00:00");
        var envelope = new AIEventEnvelope
        {
            Type = "finish",
            Data = new AIFinishEventData
            {
                Model = "anthropic/claude-haiku-4-5-20251001",
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                InputTokens = 321,
                OutputTokens = 654,
                TotalTokens = 975,
                FinishReason = "stop",
                MessageMetadata = AIFinishMessageMetadata.Create(
                    model: "anthropic/claude-haiku-4-5-20251001",
                    timestamp: timestamp,
                    gateway: new AIFinishGatewayMetadata
                    {
                        Cost = 0.005828m
                    })
            }
        };

        var finishPart = Assert.IsType<FinishUIPart>(VercelUnifiedMapper.ToUIMessagePart(envelope, "anthropic").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal("anthropic/claude-haiku-4-5-20251001", finishPart.MessageMetadata?.Model);
        Assert.Equal(timestamp, finishPart.MessageMetadata?.Timestamp);
        Assert.Equal(321, finishPart.MessageMetadata?.InputTokens);
        Assert.Equal(654, finishPart.MessageMetadata?.OutputTokens);
        Assert.Equal(975, finishPart.MessageMetadata?.TotalTokens);
        Assert.Equal(0.005828m, finishPart.MessageMetadata?.Gateway?.Cost);
    }
}
