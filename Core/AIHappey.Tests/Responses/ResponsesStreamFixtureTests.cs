using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesStreamFixtureTests
{
    private const string TypedFixturePath = "Fixtures/responses/typed/basic-response-stream.json";
    private const string RawFixturePath = "Fixtures/responses/raw/basic-response-stream.jsonl";
    private const string ProviderId = "fixture-provider";
    private const string ReasoningAndProviderToolsRawFixturePath = "Fixtures/responses/raw/openai-reasoning-and-provider-tools-response-stream.jsonl";

    [Fact]
    public void Typed_and_raw_responses_fixtures_produce_the_same_stream_part_types()
    {
        var typed = FixtureFileLoader.LoadResponseTypedFixture(TypedFixturePath);
        var raw = FixtureFileLoader.LoadResponseRawFixture(RawFixturePath);

        Assert.Equal(typed.Select(part => part.Type), raw.Select(part => part.Type));
    }

    [Fact]
    public void Typed_responses_fixture_maps_to_expected_unified_events()
    {
        var parts = FixtureFileLoader.LoadResponseTypedFixture(TypedFixturePath);

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId))
            .ToList();

        var eventTypes = unifiedEvents
            .Select(streamEvent => streamEvent.Event.Type)
            .ToList();

        FixtureAssertions.AssertContainsSubsequence(
            eventTypes,
            "text-start",
            "text-delta",
            "text-end",
            "finish");

        var textDeltaEvent = unifiedEvents.Single(streamEvent => streamEvent.Event.Type == "text-delta");
        var textDeltaData = Assert.IsType<AITextDeltaEventData>(textDeltaEvent.Event.Data);
        Assert.Equal("Hello world", textDeltaData.Delta);

        var syntheticDeltaEvent = new AIStreamEvent
        {
            ProviderId = ProviderId,
            Event = new AIEventEnvelope
            {
                Type = "response.output_text.delta",
                Data = new Dictionary<string, object?>
                {
                    ["sequence_number"] = 99,
                    ["delta"] = "Hello world",
                    ["item_id"] = "msg_fixture_1",
                    ["content_index"] = 0,
                    ["output_index"] = 0
                }
            }
        };

        var reverseMappedDelta = Assert.IsType<ResponseOutputTextDelta>(syntheticDeltaEvent.ToResponseStreamPart());
        Assert.Equal("Hello world", reverseMappedDelta.Delta);
    }

    [Fact]
    public async Task Responses_bridge_can_replay_fixture_parts_through_the_unified_runtime()
    {
        var parts = FixtureFileLoader.LoadResponseTypedFixture(TypedFixturePath);
        var provider = new FixtureResponseStreamModelProvider(ProviderId, parts);

        var request = new AIRequest
        {
            ProviderId = ProviderId,
            Model = "gpt-fixture",
            Instructions = "Return a short greeting.",
            Stream = true,
            Headers = new Dictionary<string, string>
            {
                ["x-fixture"] = "responses"
            },
            Input = new AIInput
            {
                Items = new List<AIInputItem>
                {
                    new()
                    {
                        Type = "message",
                        Role = "user",
                        Content = new List<AIContentPart>
                        {
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Say hello"
                            }
                        }
                    }
                }
            }
        };

        var unifiedEvents = await FixtureAssertions.CollectAsync(provider.StreamUnifiedViaResponsesAsync(request));

        Assert.NotEmpty(unifiedEvents);
        Assert.All(unifiedEvents, streamEvent => Assert.Equal(ProviderId, streamEvent.ProviderId));
        Assert.Equal("text-start", unifiedEvents[0].Event.Type);

        var eventTypes = unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList();
        FixtureAssertions.AssertContainsSubsequence(
            eventTypes,
            "text-start",
            "text-delta",
            "text-end",
            "finish");
    }

    [Fact]
    public void Reasoning_stream_events_preserve_provider_reasoning_item_id_in_unified_and_ui_metadata()
    {
        const string expectedReasoningItemId = "rs_04c8e0b6f86052560169e0881be7c881a39d84fb7b82c27880";
        const string reasoningProviderId = "openai";

        var parts = FixtureFileLoader.LoadResponseRawFixture(ReasoningAndProviderToolsRawFixturePath);

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(reasoningProviderId))
            .ToList();

        var reasoningEndEvent = unifiedEvents.First(streamEvent => streamEvent.Event.Type == "reasoning-end");
        var reasoningEndData = Assert.IsType<AIReasoningEndEventData>(reasoningEndEvent.Event.Data);
        var reasoningProviderMetadata = Assert.Contains(reasoningProviderId, reasoningEndData.ProviderMetadata ?? new Dictionary<string, Dictionary<string, object>>());

        Assert.Equal(expectedReasoningItemId, Assert.IsType<string>(reasoningProviderMetadata["id"]));
        Assert.Equal(expectedReasoningItemId, Assert.IsType<string>(reasoningProviderMetadata["item_id"]));

        var reasoningUiPart = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "reasoning-start" or "reasoning-delta" or "reasoning-end")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(reasoningProviderId))
            .OfType<ReasoningEndUIPart>()
            .First();

        var uiProviderMetadata = Assert.Contains(reasoningProviderId, reasoningUiPart.ProviderMetadata ?? new Dictionary<string, Dictionary<string, object>>());
        Assert.Equal(expectedReasoningItemId, Assert.IsType<string>(uiProviderMetadata["id"]));
        Assert.Equal(expectedReasoningItemId, Assert.IsType<string>(uiProviderMetadata["item_id"]));
    }
}
