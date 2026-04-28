using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AIHappey.Responses.Extensions;
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
    private const string MultipleShellCallsWithStreamingOutputFixturePath = "Fixtures/responses/raw/openai-multiple-shell-calls-with-streaming-output.jsonl";
    private const string ScalewayReasoningRawFixturePath = "Fixtures/responses/raw/scaleway-with-reasoning-streaming.jsonl";
    private const string CodeInterpreterOutputFileRawFixturePath = "Fixtures/responses/raw/xai-with-code_interpreter-output-file-stream.jsonl";

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
                Items =
                [
                    new()
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Say hello"
                            }
                        ]
                    }
                ]
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
        var reasoningProviderMetadata = Assert.Contains(reasoningProviderId, reasoningEndData.ProviderMetadata ?? []);

        var reasoningUiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "reasoning-start" or "reasoning-delta" or "reasoning-end")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(reasoningProviderId))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(reasoningUiParts);

        var reasoningUiPart = Assert.IsType<ReasoningEndUIPart>(reasoningUiParts.OfType<ReasoningEndUIPart>().First());

        var uiProviderMetadata = Assert.Contains(reasoningProviderId, reasoningUiPart.ProviderMetadata ?? []);
    }

    [Fact]
    public void Scaleway_reasoning_part_events_emit_reasoning_start_before_reasoning_deltas_for_vercel_ui_stream()
    {
        const string scalewayProviderId = "scaleway";
        const string expectedReasoningItemId = "msg_9180b40d3608ba1a";

        var parts = FixtureFileLoader.LoadResponseRawFixture(ScalewayReasoningRawFixturePath);

        var unifiedReasoningEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(scalewayProviderId))
            .Where(streamEvent => streamEvent.Event.Type is "reasoning-start" or "reasoning-delta" or "reasoning-end")
            .ToList();

        Assert.NotEmpty(unifiedReasoningEvents);
        Assert.Equal("reasoning-start", unifiedReasoningEvents[0].Event.Type);
        Assert.Equal(expectedReasoningItemId, unifiedReasoningEvents[0].Event.Id);
        Assert.All(
            unifiedReasoningEvents.Where(streamEvent => streamEvent.Event.Type == "reasoning-delta"),
            streamEvent => Assert.Equal(expectedReasoningItemId, streamEvent.Event.Id));

        FixtureAssertions.AssertContainsSubsequence(
            unifiedReasoningEvents.Select(streamEvent => streamEvent.Event.Type).ToList(),
            "reasoning-start",
            "reasoning-delta",
            "reasoning-end");

        var reasoningStartIndex = unifiedReasoningEvents.FindIndex(streamEvent => streamEvent.Event.Type == "reasoning-start");
        var firstReasoningDeltaIndex = unifiedReasoningEvents.FindIndex(streamEvent => streamEvent.Event.Type == "reasoning-delta");
        var reasoningEndIndex = unifiedReasoningEvents.FindIndex(streamEvent => streamEvent.Event.Type == "reasoning-end");

        Assert.True(reasoningStartIndex >= 0);
        Assert.True(firstReasoningDeltaIndex > reasoningStartIndex);
        Assert.True(reasoningEndIndex > firstReasoningDeltaIndex);

        var reasoningText = string.Concat(unifiedReasoningEvents
            .Where(streamEvent => streamEvent.Event.Type == "reasoning-delta")
            .Select(streamEvent => Assert.IsType<AIReasoningDeltaEventData>(streamEvent.Event.Data).Delta));

        Assert.Equal(
            "User says \"test\". Probably they just test system. Should respond politely, maybe ask how can help.",
            reasoningText);

        var uiReasoningParts = unifiedReasoningEvents
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(scalewayProviderId))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(uiReasoningParts);
        Assert.Equal("reasoning-start", uiReasoningParts[0].Type);

        var reasoningStartPart = Assert.IsType<ReasoningStartUIPart>(uiReasoningParts[0]);
        Assert.Equal(expectedReasoningItemId, reasoningStartPart.Id);
        var firstReasoningDeltaPart = Assert.IsType<ReasoningDeltaUIPart>(uiReasoningParts.First(part => part.Type == "reasoning-delta"));
        Assert.Equal(expectedReasoningItemId, firstReasoningDeltaPart.Id);
    }

    [Fact]
    public async Task Responses_unified_runtime_injects_missing_reasoning_start_before_orphan_reasoning_delta()
    {
        const string providerId = "scaleway";
        const string expectedReasoningItemId = "msg_orphan_reasoning";

        var provider = new FixtureResponseStreamModelProvider(
            providerId,
            [
                new ResponseReasoningTextDelta
                {
                    ItemId = expectedReasoningItemId,
                    Delta = "orphan reasoning delta",
                    OutputIndex = 0,
                    ContentIndex = 0,
                    SequenceNumber = 1
                }
            ]);

        var request = new AIRequest
        {
            ProviderId = providerId,
            Model = "gpt-oss-120b",
            Stream = true
        };

        var unifiedEvents = await FixtureAssertions.CollectAsync(provider.StreamUnifiedViaResponsesAsync(request));
        var reasoningEvents = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "reasoning-start" or "reasoning-delta")
            .ToList();

        Assert.Collection(
            reasoningEvents,
            streamEvent =>
            {
                Assert.Equal("reasoning-start", streamEvent.Event.Type);
                Assert.Equal(expectedReasoningItemId, streamEvent.Event.Id);
            },
            streamEvent =>
            {
                Assert.Equal("reasoning-delta", streamEvent.Event.Type);
                Assert.Equal(expectedReasoningItemId, streamEvent.Event.Id);
                Assert.Equal("orphan reasoning delta", Assert.IsType<AIReasoningDeltaEventData>(streamEvent.Event.Data).Delta);
            });

        var uiParts = reasoningEvents
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(providerId))
            .ToList();

        Assert.Equal(["reasoning-start", "reasoning-delta"], uiParts.Select(part => part.Type).ToList());
    }

    [Fact]
    public void Multiple_shell_call_outputs_emit_preliminary_then_final_tool_output_events_for_each_tool_call()
    {
        const string providerId = "openai";

        var parts = FixtureFileLoader.LoadResponseRawFixture(MultipleShellCallsWithStreamingOutputFixturePath);

        var shellToolInputs = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(providerId))
            .Where(streamEvent => streamEvent.Event.Type == "tool-input-available")
            .Select(streamEvent => Assert.IsType<AIToolInputAvailableEventData>(streamEvent.Event.Data))
            .Where(data => string.Equals(data.ToolName, "shell_call", StringComparison.Ordinal))
            .ToList();

        var toolOutputEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(providerId))
            .Where(streamEvent => streamEvent.Event.Type == "tool-output-available")
            .Select(streamEvent => new
            {
                ToolCallId = streamEvent.Event.Id,
                Data = Assert.IsType<AIToolOutputAvailableEventData>(streamEvent.Event.Data)
            })
            .Where(streamEvent => IsShellToolOutput(streamEvent.Data, providerId))
            .ToList();

        var toolCallGroups = toolOutputEvents
            .GroupBy(streamEvent => streamEvent.ToolCallId)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(shellToolInputs);
        Assert.Equal(shellToolInputs.Count, toolCallGroups.Count);

        foreach (var group in toolCallGroups)
        {
            Assert.Contains(group, item => item.Data.Preliminary == true);
            Assert.Equal(false, group.Last().Data.Preliminary);
        }
    }

    [Fact]
    public void Completed_response_recovery_emits_final_shell_tool_outputs_for_each_shell_call()
    {
        const string providerId = "openai";

        var parts = FixtureFileLoader.LoadResponseRawFixture(MultipleShellCallsWithStreamingOutputFixturePath)
            .Where(part => !IsLiveShellOutputStreamingPart(part))
            .ToList();

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(providerId))
            .Where(streamEvent => streamEvent.Event.Type is "tool-input-available" or "tool-output-available")
            .ToList();

        var shellToolInputs = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "tool-input-available")
            .Select(streamEvent => new
            {
                ToolCallId = streamEvent.Event.Id,
                Data = Assert.IsType<AIToolInputAvailableEventData>(streamEvent.Event.Data)
            })
            .Where(streamEvent => string.Equals(streamEvent.Data.ToolName, "shell_call", StringComparison.Ordinal))
            .OrderBy(streamEvent => streamEvent.ToolCallId, StringComparer.Ordinal)
            .ToList();

        var shellFinalToolOutputs = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "tool-output-available")
            .Select(streamEvent => new
            {
                ToolCallId = streamEvent.Event.Id,
                Data = Assert.IsType<AIToolOutputAvailableEventData>(streamEvent.Event.Data)
            })
            .Where(streamEvent => streamEvent.Data.Preliminary is false or null)
            .Where(streamEvent => IsShellToolOutput(streamEvent.Data, providerId))
            .OrderBy(streamEvent => streamEvent.ToolCallId, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(shellToolInputs);
        Assert.Equal(shellToolInputs.Count, shellFinalToolOutputs.Count);
        Assert.Equal(
            shellToolInputs.Select(streamEvent => streamEvent.ToolCallId).ToList(),
            shellFinalToolOutputs.Select(streamEvent => streamEvent.ToolCallId).ToList());
    }

    [Fact]
    public void Code_interpreter_output_files_emit_unified_file_events_without_changing_tool_outputs()
    {
        const string providerId = "xai";

        var unifiedEvents = FixtureFileLoader.LoadResponseRawFixture(CodeInterpreterOutputFileRawFixturePath)
            .SelectMany(part => part.ToUnifiedStreamEvent(providerId))
            .ToList();

        var codeInterpreterToolOutputs = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "tool-output-available")
            .Select(streamEvent => new
            {
                streamEvent.Event.Id,
                Data = Assert.IsType<AIToolOutputAvailableEventData>(streamEvent.Event.Data)
            })
            .Where(streamEvent => streamEvent.Id?.StartsWith("ci_", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(2, codeInterpreterToolOutputs.Count);
        Assert.All(codeInterpreterToolOutputs, streamEvent => Assert.True(streamEvent.Data.ProviderExecuted));

        var fileEvents = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "file")
            .Select(streamEvent => Assert.IsType<AIFileEventData>(streamEvent.Event.Data))
            .ToList();

        var fileEvent = Assert.Single(fileEvents);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileEvent.MediaType);
        Assert.Equal("eenvoudig_document.docx", fileEvent.Filename);
        Assert.StartsWith($"data:{fileEvent.MediaType};base64,UEsDB", fileEvent.Url, StringComparison.Ordinal);

        var providerMetadata = Assert.Contains(providerId, fileEvent.ProviderMetadata ?? []);
        Assert.Equal("code_interpreter", Assert.IsType<string>(providerMetadata["tool_name"]));
        Assert.Equal("/home/workdir/eenvoudig_document.docx", Assert.IsType<string>(providerMetadata["file_path"]));
        Assert.Equal(37047L, Convert.ToInt64(providerMetadata["size"]));

        var uiFilePart = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "file")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(providerId))
            .OfType<FileUIPart>()
            .Single();

        Assert.Equal(fileEvent.MediaType, uiFilePart.MediaType);
        Assert.Equal(fileEvent.Url, uiFilePart.Url);
    }

    [Fact]
    public async Task Responses_sse_parser_accumulates_multiline_data_blocks_before_deserializing()
    {
        const string providerId = "openai";

        var handler = new StaticResponseHttpMessageHandler(_ => CreateStreamingResponse(CreateMultilineShellSseFixture()));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test/")
        };

        var responseParts = await FixtureAssertions.CollectAsync(httpClient.GetResponsesUpdates(
            new AIHappey.Responses.ResponseRequest
            {
                Model = "gpt-test",
                Stream = true
            },
            providerId: providerId,
            capture: null));

        var uiParts = responseParts
            .SelectMany(part => part.ToUnifiedStreamEvent(providerId))
            .Where(streamEvent => streamEvent.Event.Type is "tool-input-available" or "tool-output-available")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(providerId))
            .ToList();

        var toolInputPart = Assert.IsType<ToolCallPart>(Assert.Single(uiParts.OfType<ToolCallPart>()));
        Assert.Equal("sh_multiline", toolInputPart.ToolCallId);

        var toolOutputPart = Assert.IsType<ToolOutputAvailablePart>(Assert.Single(uiParts.OfType<ToolOutputAvailablePart>()));
        Assert.Equal("sh_multiline", toolOutputPart.ToolCallId);
        Assert.Equal(false, toolOutputPart.Preliminary);
    }

    private static bool IsLiveShellOutputStreamingPart(ResponseStreamPart part)
        => part is ResponseOutputItemDone { Item.Type: "shell_call_output" }
            || part is ResponseUnknownEvent { Type: "response.shell_call_output_content.delta" or "response.shell_call_output_content.done" };

    private static bool IsShellToolOutput(AIToolOutputAvailableEventData data, string providerId)
        => data.ProviderMetadata?.TryGetValue(providerId, out var providerMetadata) == true
            && providerMetadata.TryGetValue("tool_name", out var toolName)
            && string.Equals(toolName?.ToString(), "shell_call", StringComparison.Ordinal);

    private static HttpResponseMessage CreateStreamingResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static string CreateMultilineShellSseFixture()
        => string.Join('\n',
        [
            "event: response.output_item.added",
            "data: {\"type\":\"response.output_item.added\",",
            "data: \"item\":{\"id\":\"sh_multiline\",\"type\":\"shell_call\",\"status\":\"in_progress\",\"call_id\":\"call_multiline\",\"action\":{\"commands\":[]}},",
            "data: \"output_index\":0,\"sequence_number\":1}",
            string.Empty,
            "event: response.output_item.done",
            "data: {\"type\":\"response.output_item.done\",",
            "data: \"item\":{\"id\":\"sh_multiline\",\"type\":\"shell_call\",\"status\":\"completed\",\"call_id\":\"call_multiline\",\"action\":{\"commands\":[\"echo hello\"]}},",
            "data: \"output_index\":0,\"sequence_number\":2}",
            string.Empty,
            "event: response.output_item.added",
            "data: {\"type\":\"response.output_item.added\",",
            "data: \"item\":{\"id\":\"sho_multiline\",\"type\":\"shell_call_output\",\"status\":\"in_progress\",\"call_id\":\"call_multiline\",\"output\":[]},",
            "data: \"output_index\":1,\"sequence_number\":3}",
            string.Empty,
            "event: response.output_item.done",
            "data: {\"type\":\"response.output_item.done\",",
            "data: \"item\":{\"id\":\"sho_multiline\",\"type\":\"shell_call_output\",\"status\":\"completed\",\"call_id\":\"call_multiline\",\"output\":[{\"outcome\":{\"type\":\"exit\",\"exit_code\":0},\"stderr\":\"\",\"stdout\":\"hello\"}]},",
            "data: \"output_index\":1,\"sequence_number\":4}",
            string.Empty,
            "data: [DONE]",
            string.Empty
        ]);

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
