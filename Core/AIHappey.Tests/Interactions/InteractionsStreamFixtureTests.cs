using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsStreamFixtureTests
{
    private const string RawFixturePath = "Fixtures/interactions/raw/interactions-with-tools.jsonl";
    private const string ProviderId = "fixture-provider";
    private const string ToolCallId = "986aolc5";

    [Fact]
    public void Interactions_code_execution_fixture_maps_to_provider_executed_unified_tool_events_with_input_and_output_payloads()
    {
        var unifiedEvents = LoadUnifiedEvents();
        var eventTypes = unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList();

        FixtureAssertions.AssertContainsSubsequence(
            eventTypes,
            "reasoning-start",
            "reasoning-end",
            "tool-input-start",
            "tool-input-delta",
            "tool-input-available",
            "finish");

        var toolInputAvailable = Assert.IsType<AIToolInputAvailableEventData>(
            Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-input-available").Event.Data);
        Assert.Equal("github_rest_countries_get_detail", toolInputAvailable.ToolName);
        Assert.False(toolInputAvailable.ProviderExecuted);

        var inputJson = JsonSerializer.SerializeToElement(toolInputAvailable.Input, JsonSerializerOptions.Web);
        Assert.Equal("PL", inputJson.GetProperty("cca").GetString());

    }

    [Fact]
    public void Interactions_code_execution_fixture_maps_to_expected_vercel_ui_stream_parts()
    {
        var uiParts = LoadUnifiedEvents()
            .Where(streamEvent => streamEvent.Event.Type is
                "reasoning-start" or
                "reasoning-end" or
                "tool-input-start" or
                "tool-input-delta" or
                "tool-input-available" or
                "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        FixtureAssertions.AssertContainsSubsequence(
            uiParts.Select(part => part.Type).ToList(),
            "reasoning-start",
            "reasoning-end",
            "tool-input-start",
            "tool-input-delta",
            "tool-input-available",
            "finish");

        Assert.Single(uiParts.OfType<ReasoningStartUIPart>());
        Assert.Single(uiParts.OfType<ReasoningEndUIPart>());
        Assert.Single(uiParts.OfType<ToolCallStreamingStartPart>());
        Assert.Single(uiParts.OfType<ToolCallDeltaPart>());
        Assert.Single(uiParts.OfType<ToolCallPart>());
        Assert.Single(uiParts.OfType<FinishUIPart>());

        var toolCallPart = Assert.IsType<ToolCallPart>(uiParts.Single(part => part.Type == "tool-input-available"));
        Assert.Equal(ToolCallId, toolCallPart.ToolCallId);
        Assert.Equal("github_rest_countries_get_detail", toolCallPart.ToolName);
        Assert.False(toolCallPart.ProviderExecuted);

        var toolInput = JsonSerializer.SerializeToElement(toolCallPart.Input, JsonSerializerOptions.Web);
        Assert.Equal("PL", toolInput.GetProperty("cca").GetString());
       
        var finishPart = Assert.IsType<FinishUIPart>(uiParts.Single(part => part.Type == "finish"));
        Assert.Equal("stop", finishPart.FinishReason);
    }

    [Fact]
    public void Interactions_tool_signature_delta_on_function_call_index_does_not_emit_unmatched_reasoning_end()
    {
        var parts = new List<InteractionStreamEventPart>
        {
            new InteractionStepStartEvent
            {
                Index = 1,
                Step = new InteractionFunctionCallContent
                {
                    Id = ToolCallId,
                    Name = "github_rest_countries_get_detail",
                    Arguments = JsonSerializer.Deserialize<JsonElement>("{}")
                }
            },
            new InteractionStepDeltaEvent
            {
                Index = 1,
                Delta = new InteractionContentDeltaData
                {
                    Type = "arguments_delta",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["arguments"] = JsonSerializer.SerializeToElement("{\"cca\":\"PL\"}")
                    }
                }
            },
            new InteractionStepDeltaEvent
            {
                Index = 1,
                Delta = new InteractionContentDeltaData
                {
                    Type = "thought_signature",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["signature"] = JsonSerializer.SerializeToElement("tool-index-signature")
                    }
                }
            },
            new InteractionStepStopEvent
            {
                Index = 1
            }
        };

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId))
            .ToList();

        Assert.DoesNotContain(unifiedEvents, streamEvent =>
            streamEvent.Event.Type == "reasoning-end"
            && string.Equals(streamEvent.Event.Id, "interactions-content-1", StringComparison.Ordinal));

        FixtureAssertions.AssertContainsSubsequence(
            unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList(),
            "tool-input-start",
            "tool-input-delta",
            "tool-input-available");
    }


    private static List<AIStreamEvent> LoadUnifiedEvents()
        => FixtureFileLoader.LoadInteractionRawFixture(RawFixturePath)
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId))
            .ToList();
}
