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
    private const string AntigravityRawFixturePath = "Fixtures/interactions/raw/interactions-antigravity-agent-stream.jsonl";
    private const string VideoOutputRawFixturePath = "Fixtures/interactions/raw/interactions-with-video-output.jsonl";
    private const string FailedReasoningStartFixturePath = "Fixtures/interactions/raw/failed-reasoning-start.-missing.jsonl";
    private const string FailedTextStartFixturePath = "Fixtures/interactions/raw/failed-text-start-missing.jsonl";
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

    [Theory]
    [InlineData(FailedReasoningStartFixturePath)]
    [InlineData(FailedTextStartFixturePath)]
    public void Interactions_failed_fixtures_do_not_emit_orphan_text_or_reasoning_end_events(string rawFixturePath)
    {
        var unifiedEvents = LoadUnifiedEvents(rawFixturePath);
        AssertNoOrphanEndEvents(unifiedEvents, "text-start", "text-end");
        AssertNoOrphanEndEvents(unifiedEvents, "reasoning-start", "reasoning-end");
    }

    [Fact]
    public void Interactions_model_output_thought_step_maps_to_reasoning_events_not_text_events()
    {
        var parts = new List<InteractionStreamEventPart>
        {
            new InteractionStepStartEvent
            {
                Index = 0,
                Step = new InteractionModelOutputStep
                {
                    Content =
                    [
                        new InteractionThoughtContent
                        {
                            Signature = "sig-1"
                        }
                    ]
                }
            },
            new InteractionStepDeltaEvent
            {
                Index = 0,
                Delta = new InteractionContentDeltaData
                {
                    Type = "thought_summary",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["content"] = JsonSerializer.SerializeToElement(new InteractionTextContent { Text = "thinking" }),
                        ["signature"] = JsonSerializer.SerializeToElement("sig-1")
                    }
                }
            },
            new InteractionStepStopEvent
            {
                Index = 0
            }
        };

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId))
            .ToList();

        FixtureAssertions.AssertContainsSubsequence(
            unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList(),
            "reasoning-start",
            "reasoning-delta",
            "reasoning-end");

        Assert.DoesNotContain(unifiedEvents, streamEvent =>
            string.Equals(streamEvent.Event.Type, "text-start", StringComparison.Ordinal)
            && string.Equals(streamEvent.Event.Id, "interactions-content-0", StringComparison.Ordinal));

        Assert.DoesNotContain(unifiedEvents, streamEvent =>
            string.Equals(streamEvent.Event.Type, "text-end", StringComparison.Ordinal)
            && string.Equals(streamEvent.Event.Id, "interactions-content-0", StringComparison.Ordinal));
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
    public void Interactions_provider_executed_function_result_fixture_maps_to_unified_tool_input_and_output_events()
    {
        const string toolCallId = "nlufjglg";

        var unifiedEvents = LoadUnifiedEvents(AntigravityRawFixturePath);

        FixtureAssertions.AssertContainsSubsequence(
            unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList(),
            "tool-input-start",
            "tool-input-delta",
            "tool-input-available",
            "tool-output-available",
            "finish");

        var toolInputEvent = Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-input-available");
        Assert.Equal(toolCallId, toolInputEvent.Event.Id);

        var toolInput = Assert.IsType<AIToolInputAvailableEventData>(toolInputEvent.Event.Data);
        Assert.Equal("write_file", toolInput.ToolName);
        var inputJson = JsonSerializer.SerializeToElement(toolInput.Input, JsonSerializerOptions.Web);
        Assert.Equal("geocities.html", inputJson.GetProperty("path").GetString());

        var toolOutputEvent = Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-output-available");
        Assert.Equal(toolCallId, toolOutputEvent.Event.Id);

        var toolOutput = Assert.IsType<AIToolOutputAvailableEventData>(toolOutputEvent.Event.Data);
        Assert.True(toolOutput.ProviderExecuted);
        Assert.Equal("write_file", toolOutput.ToolName);

        var outputJson = JsonSerializer.SerializeToElement(toolOutput.Output, JsonSerializerOptions.Web);
        Assert.Equal("{\"success\":true}", outputJson[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Interactions_provider_executed_function_result_fixture_maps_to_expected_vercel_ui_stream_parts()
    {
        const string toolCallId = "nlufjglg";

        var uiParts = LoadUnifiedEvents(AntigravityRawFixturePath)
            .Where(streamEvent => streamEvent.Event.Type is
                "tool-input-start" or
                "tool-input-delta" or
                "tool-input-available" or
                "tool-output-available" or
                "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        FixtureAssertions.AssertContainsSubsequence(
            uiParts.Select(part => part.Type).ToList(),
            "tool-input-start",
            "tool-input-delta",
            "tool-input-available",
            "tool-output-available",
            "finish");

        var toolCallPart = Assert.IsType<ToolCallPart>(uiParts.Single(part => part.Type == "tool-input-available"));
        Assert.Equal(toolCallId, toolCallPart.ToolCallId);

        var toolOutputPart = Assert.IsType<ToolOutputAvailablePart>(uiParts.Single(part => part.Type == "tool-output-available"));
        Assert.Equal(toolCallId, toolOutputPart.ToolCallId);
        Assert.True(toolOutputPart.ProviderExecuted);

        var outputJson = JsonSerializer.SerializeToElement(toolOutputPart.Output, JsonSerializerOptions.Web);
        Assert.Equal("{\"success\":true}", outputJson.GetProperty("structuredContent")[0].GetProperty("text").GetString());

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

    [Fact]
    public void Interactions_google_search_signature_deltas_do_not_emit_unmatched_reasoning_end()
    {
        const string searchCallId = "dfqp0d01";

        var parts = new List<InteractionStreamEventPart>
        {
            new InteractionStepStartEvent
            {
                Index = 0,
                Step = new InteractionThoughtContent()
            },
            new InteractionStepDeltaEvent
            {
                Index = 0,
                Delta = new InteractionContentDeltaData
                {
                    Type = "thought_summary",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["content"] = JsonSerializer.SerializeToElement(new InteractionTextContent
                        {
                            Text = "Thinking about search terms."
                        })
                    }
                }
            },
            new InteractionStepStopEvent
            {
                Index = 0
            },
            new InteractionStepStartEvent
            {
                Index = 1,
                Step = new InteractionGoogleSearchCallContent
                {
                    Id = searchCallId
                }
            },
            new InteractionStepDeltaEvent
            {
                Index = 1,
                Delta = new InteractionContentDeltaData
                {
                    Type = "google_search_call",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["signature"] = JsonSerializer.SerializeToElement("google-search-call-signature"),
                        ["arguments"] = JsonSerializer.SerializeToElement(new
                        {
                            queries = new[] { "latest war news" }
                        })
                    }
                }
            },
            new InteractionStepStopEvent
            {
                Index = 1
            },
            new InteractionStepStartEvent
            {
                Index = 2,
                Step = new InteractionGoogleSearchResultContent
                {
                    CallId = searchCallId
                }
            },
            new InteractionStepDeltaEvent
            {
                Index = 2,
                Delta = new InteractionContentDeltaData
                {
                    Type = "google_search_result",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["signature"] = JsonSerializer.SerializeToElement("google-search-result-signature"),
                        ["result"] = JsonSerializer.SerializeToElement(new[]
                        {
                            new InteractionGoogleSearchResult
                            {
                                SearchSuggestions = "latest war news"
                            }
                        }),
                        ["is_error"] = JsonSerializer.SerializeToElement(false)
                    }
                }
            },
            new InteractionStepStopEvent
            {
                Index = 2
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
            "reasoning-start",
            "reasoning-delta",
            "reasoning-end",
            "tool-input-available",
            "tool-output-available");

        var toolInputAvailable = Assert.IsType<AIToolInputAvailableEventData>(
            Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-input-available").Event.Data);
        Assert.Equal("google_search", toolInputAvailable.ToolName);
        Assert.True(toolInputAvailable.ProviderExecuted);
        Assert.Equal(searchCallId, Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-input-available").Event.Id);
        Assert.Equal(
            "google-search-call-signature",
            toolInputAvailable.ProviderMetadata?[ProviderId]["signature"].ToString());

        var toolOutputAvailable = Assert.IsType<AIToolOutputAvailableEventData>(
            Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-output-available").Event.Data);
        Assert.True(toolOutputAvailable.ProviderExecuted);
        Assert.Equal(searchCallId, Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-output-available").Event.Id);
        Assert.Equal(
            "google-search-result-signature",
            toolOutputAvailable.ProviderMetadata?[ProviderId]["signature"].ToString());
    }

    [Fact]
    public void Interactions_code_execution_result_uses_original_tool_call_id()
    {
        const string codeCallId = "08ae9l71";

        var parts = new List<InteractionStreamEventPart>
        {
            new InteractionStepStartEvent
            {
                Index = 1,
                Step = new InteractionCodeExecutionCallContent
                {
                    Id = codeCallId
                }
            },
            new InteractionStepDeltaEvent
            {
                Index = 1,
                Delta = new InteractionContentDeltaData
                {
                    Type = "code_execution_call",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["arguments"] = JsonSerializer.SerializeToElement(new InteractionCodeExecutionCallArguments
                        {
                            Language = "python",
                            Code = "print('hello')"
                        })
                    }
                }
            },
            new InteractionStepStopEvent
            {
                Index = 1
            },
            new InteractionStepStartEvent
            {
                Index = 2,
                Step = new InteractionCodeExecutionResultContent
                {
                    CallId = codeCallId
                }
            },
            new InteractionStepDeltaEvent
            {
                Index = 2,
                Delta = new InteractionContentDeltaData
                {
                    Type = "code_execution_result",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["result"] = JsonSerializer.SerializeToElement("hello\n"),
                        ["is_error"] = JsonSerializer.SerializeToElement(false)
                    }
                }
            },
            new InteractionStepStopEvent
            {
                Index = 2
            }
        };

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId))
            .ToList();

        var toolInputEvent = Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-input-available");
        Assert.Equal(codeCallId, toolInputEvent.Event.Id);

        var toolOutputEvent = Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "tool-output-available");
        Assert.Equal(codeCallId, toolOutputEvent.Event.Id);

        var toolOutputAvailable = Assert.IsType<AIToolOutputAvailableEventData>(toolOutputEvent.Event.Data);
        Assert.Equal(
            codeCallId,
            toolOutputAvailable.ProviderMetadata?[ProviderId]["tool_use_id"].ToString());
    }

    [Fact]
    public void Interactions_video_output_fixture_maps_to_file_event_and_vercel_file_ui_part()
    {
        var unifiedEvents = LoadUnifiedEvents(VideoOutputRawFixturePath);

        FixtureAssertions.AssertContainsSubsequence(
            unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList(),
            "text-start",
            "text-delta",
            "text-delta",
            "text-delta",
            "text-end",
            "file",
            "finish");

        var fileEvent = Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "file");
        Assert.Equal("interactions-video-2", fileEvent.Event.Id);

        var fileEventData = Assert.IsType<AIFileEventData>(fileEvent.Event.Data);
        Assert.Equal("video/mp4", fileEventData.MediaType);
        Assert.Equal("interactions-video-2.mp4", fileEventData.Filename);
        Assert.StartsWith("data:video/mp4;base64,AAAAIGZ0eXBpc29t", fileEventData.Url, StringComparison.Ordinal);

        var providerMetadata = Assert.Contains(ProviderId, fileEventData.ProviderMetadata ?? []);
        Assert.Equal("interaction_video_file", Assert.IsType<string>(providerMetadata["type"]));
        Assert.Equal("video", Assert.IsType<string>(providerMetadata["interactions.content.type"]));
        Assert.Equal(2, Convert.ToInt32(providerMetadata["interactions.content.index"]));
        Assert.Equal("video/mp4", Assert.IsType<string>(providerMetadata["mime_type"]));

        var uiFilePart = fileEvent.Event
            .ToUIMessagePart(ProviderId)
            .OfType<FileUIPart>()
            .Single();

        Assert.Equal(fileEventData.MediaType, uiFilePart.MediaType);
        Assert.Equal(fileEventData.Url, uiFilePart.Url);
        Assert.Contains(ProviderId, uiFilePart.ProviderMetadata ?? []);
    }


    private static List<AIStreamEvent> LoadUnifiedEvents(string rawFixturePath = RawFixturePath)
        => FixtureFileLoader.LoadInteractionRawFixture(rawFixturePath)
            .SelectMany(part => part.ToUnifiedStreamEvent(ProviderId))
            .ToList();

    private static void AssertNoOrphanEndEvents(
        IEnumerable<AIStreamEvent> streamEvents,
        string startType,
        string endType)
    {
        var startedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var streamEvent in streamEvents)
        {
            if (string.Equals(streamEvent.Event.Type, startType, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(streamEvent.Event.Id))
                    startedIds.Add(streamEvent.Event.Id!);
                continue;
            }

            if (!string.Equals(streamEvent.Event.Type, endType, StringComparison.Ordinal))
                continue;

            Assert.False(
                string.IsNullOrWhiteSpace(streamEvent.Event.Id),
                $"Expected '{endType}' to have an ID.");

            Assert.Contains(
                streamEvent.Event.Id!,
                startedIds);

            startedIds.Remove(streamEvent.Event.Id!);
        }
    }
}
