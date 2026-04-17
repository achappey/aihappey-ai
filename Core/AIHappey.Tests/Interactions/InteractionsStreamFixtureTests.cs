using System.Text.Json;
using AIHappey.Interactions.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsStreamFixtureTests
{
    private const string RawFixturePath = "Fixtures/interactions/raw/interactions-stream-with-code-execution.jsonl";
    private const string ProviderId = "fixture-provider";
    private const string ToolCallId = "pfdjxngl";

    [Fact]
    public void Interactions_code_execution_fixture_maps_to_provider_executed_unified_tool_events_with_input_and_output_payloads()
    {
        var unifiedEvents = LoadUnifiedEvents();
        var eventTypes = unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList();

        FixtureAssertions.AssertContainsSubsequence(
            eventTypes,
            "text-start",
            "text-delta",
            "text-end",
            "tool-input-available",
            "tool-output-available",
            "text-start",
            "text-delta",
            "text-end",
            "finish");

        var toolInputAvailable = Assert.IsType<AIToolInputAvailableEventData>(
            Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-input-available").Event.Data);
        Assert.Equal("code_execution", toolInputAvailable.ToolName);
        Assert.True(toolInputAvailable.ProviderExecuted);

        var inputJson = JsonSerializer.SerializeToElement(toolInputAvailable.Input, JsonSerializerOptions.Web);
        Assert.Equal("python", inputJson.GetProperty("language").GetString());
        Assert.Contains("to_markdown(index=False)", inputJson.GetProperty("code").GetString());

        //    var inputDeltaJson = JsonDocument.Parse(toolInputDelta.InputTextDelta).RootElement;
        //   Assert.Equal(inputJson.GetProperty("language").GetString(), inputDeltaJson.GetProperty("language").GetString());
        //  Assert.Equal(inputJson.GetProperty("code").GetString(), inputDeltaJson.GetProperty("code").GetString());

        var toolOutputAvailable = Assert.IsType<AIToolOutputAvailableEventData>(
            Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-output-available").Event.Data);
        Assert.Equal("code_execution", toolOutputAvailable.ToolName);
        Assert.True(toolOutputAvailable.ProviderExecuted);

        var outputJson = JsonSerializer.SerializeToElement(toolOutputAvailable.Output, JsonSerializerOptions.Web);
        Assert.Equal("code_execution_result", outputJson.GetProperty("type").GetString());
        Assert.Contains("|   Getal |   Kwadraat |", outputJson.GetProperty("stdout").GetString());
        Assert.Equal(0, outputJson.GetProperty("return_code").GetInt32());
        Assert.Empty(outputJson.GetProperty("content").EnumerateArray());

        var providerMetadata = Assert.Contains(ProviderId, toolOutputAvailable.ProviderMetadata ?? []);
        Assert.Equal("code_execution", Assert.IsType<string>(providerMetadata["tool_name"]));
        Assert.Equal(ToolCallId, Assert.IsType<string>(providerMetadata["tool_use_id"]));
    }

    [Fact]
    public void Interactions_code_execution_fixture_maps_to_expected_vercel_ui_stream_parts()
    {
        var uiParts = LoadUnifiedEvents()
            .Where(streamEvent => streamEvent.Event.Type is
                "text-start" or
                "text-delta" or
                "text-end" or
                "tool-input-available" or
                "tool-output-available" or
                "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        FixtureAssertions.AssertContainsSubsequence(
            uiParts.Select(part => part.Type).ToList(),
            "text-start",
            "text-delta",
            "text-end",
            "tool-input-available",
            "tool-output-available",
            "text-start",
            "text-delta",
            "text-end",
            "finish");

        Assert.Equal(2, uiParts.OfType<TextStartUIMessageStreamPart>().Count());
        Assert.Equal(2, uiParts.OfType<TextEndUIMessageStreamPart>().Count());
        Assert.Single(uiParts.OfType<ToolCallPart>());
        Assert.Single(uiParts.OfType<ToolOutputAvailablePart>());
        Assert.Single(uiParts.OfType<FinishUIPart>());

        var toolCallPart = Assert.IsType<ToolCallPart>(uiParts.Single(part => part.Type == "tool-input-available"));
        Assert.Equal(ToolCallId, toolCallPart.ToolCallId);
        Assert.Equal("code_execution", toolCallPart.ToolName);
        Assert.True(toolCallPart.ProviderExecuted);

        var toolInput = JsonSerializer.SerializeToElement(toolCallPart.Input, JsonSerializerOptions.Web);
        Assert.Equal("python", toolInput.GetProperty("language").GetString());
        Assert.Contains("for i in range(1, 11)", toolInput.GetProperty("code").GetString());

        var toolOutputPart = Assert.IsType<ToolOutputAvailablePart>(uiParts.Single(part => part.Type == "tool-output-available"));
        Assert.Equal(ToolCallId, toolOutputPart.ToolCallId);
        Assert.True(toolOutputPart.ProviderExecuted);
       
        var finishPart = Assert.IsType<FinishUIPart>(uiParts.Single(part => part.Type == "finish"));
        Assert.Equal("stop", finishPart.FinishReason);
    }


    private static List<AIStreamEvent> LoadUnifiedEvents()
        => FixtureFileLoader.LoadInteractionRawFixture(RawFixturePath)
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId))
            .ToList();
}
