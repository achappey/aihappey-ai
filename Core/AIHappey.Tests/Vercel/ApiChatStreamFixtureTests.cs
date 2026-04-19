using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class ApiChatStreamFixtureTests
{
    private const string GitHubRawFixturePath = "Fixtures/chat-completions/raw/github-chat-completions-stream.jsonl";
    private const string GroqErrorRawFixturePath = "Fixtures/chat-completions/raw/groq-error-completions-stream.jsonl";
    private const string OpenAiWebSearchRawFixturePath = "Fixtures/chat-completions/raw/openai-web-search-chat-completions.jsonl";
    private const string SonarWebSearchRawFixturePath = "Fixtures/chat-completions/raw/sonar-web-search-completions-stream.jsonl";
    private const string GitHubProviderId = "github";
    private const string OpenAiProviderId = "openai";
    private const string ReasoningMessagesRawFixturePath = "Fixtures/messages/raw/reasoning-messages-stream.jsonl";
    private const string ReasoningAndProviderToolCallsMessagesRawFixturePath = "Fixtures/messages/raw/reasoning-and-provider-tool-calls-stream.jsonl";
    private const string MultipleShellCallsWithStreamingOutputFixturePath = "Fixtures/responses/raw/openai-multiple-shell-calls-with-streaming-output.jsonl";
    private const string ProviderId = "fixture-provider";
    private const string GroqProviderId = "groq";
    private const string PerplexityProviderId = "perplexity";

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
                    usage: JsonSerializer.SerializeToElement(new
                    {
                        input_tokens = 321,
                        output_tokens = 654,
                        total_tokens = 975,
                        input_tokens_details = new
                        {
                            cached_tokens = 120
                        }
                    }),
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
        Assert.Equal(321, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(654, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(975, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(0.005828m, finishPart.MessageMetadata?.Gateway?.Cost);

        var providerMetadata = Assert.Contains("anthropic", finishPart.MessageMetadata?.AdditionalProperties ?? []);
        var providerUsage = providerMetadata.GetProperty("usage");
        Assert.Equal(321, providerUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(120, providerUsage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
    }

    [Fact]
    public void Messages_reasoning_unified_events_map_to_ui_parts_with_provider_scoped_signature_metadata()
    {
        var parts = FixtureFileLoader.LoadMessageRawFixture(ReasoningMessagesRawFixturePath);
        var mappingState = new MessagesUnifiedMapper.MessagesStreamMappingState();
        var originalSignature = parts.Single(part => part.Delta?.Type == "signature_delta").Delta?.Signature;

        var reasoningUiParts = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, mappingState))
            .Where(streamEvent => streamEvent.Event.Type is "reasoning-start" or "reasoning-delta" or "reasoning-end")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(reasoningUiParts);

        Assert.Equal(
            [
                "reasoning-start",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-end"
            ],
            reasoningUiParts.Select(part => part.Type).ToList());

        var reasoningDeltas = reasoningUiParts
            .OfType<ReasoningDeltaUIPart>()
            .Select(part => part.Delta)
            .ToList();

        Assert.Equal(
            "The user is asking me to create a poem about Groningen. This is a straightforward creative writing request. Groningen is a city in the northern Netherlands.\n\nThe user's preferred language is Dutch (nl), so I should write the poem in Dutch.\n\nLet me create an original poem about Groningen in Dutch. I'll use markdown formatting as instructed.",
            string.Concat(reasoningDeltas));

        var reasoningStartPart = Assert.IsType<ReasoningStartUIPart>(reasoningUiParts[0]);
        Assert.Null(reasoningStartPart.ProviderMetadata);

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(reasoningUiParts[^1]);
        var providerMetadata = Assert.Contains(ProviderId, reasoningEndPart.ProviderMetadata ?? []);

        Assert.Equal(originalSignature, Assert.IsType<string>(providerMetadata["signature"]));
    }

    [Fact]
    public void Messages_reasoning_and_provider_tool_calls_fixture_maps_to_expected_vercel_ui_stream_parts()
    {
        var parts = FixtureFileLoader.LoadMessageRawFixture(ReasoningAndProviderToolCallsMessagesRawFixturePath);
        var mappingState = new MessagesUnifiedMapper.MessagesStreamMappingState();
        var originalSignature = parts.Single(part => part.Delta?.Type == "signature_delta").Delta?.Signature;
        var toolUseId = parts.Single(part => part.ContentBlock?.Type == "server_tool_use").ContentBlock?.Id;

        var uiParts = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, mappingState))
            .Where(streamEvent => streamEvent.Event.Type is
                "reasoning-start" or
                "reasoning-delta" or
                "reasoning-end" or
                "tool-input-start" or
                "tool-input-delta" or
                "tool-input-available" or
                "tool-output-available" or
                "source-url" or
                "text-start" or
                "text-delta" or
                "text-end" or
                "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(uiParts);

        FixtureAssertions.AssertContainsSubsequence(
            uiParts.Select(part => part.Type).ToList(),
            "reasoning-start",
            "reasoning-delta",
            "reasoning-end",
            "tool-input-start",
            "tool-input-delta",
            "tool-input-available",
            "tool-output-available",
            "text-start",
            "source-url",
            "text-delta",
            "text-end",
            "finish");

        Assert.Single(uiParts.OfType<ReasoningStartUIPart>());
        Assert.Single(uiParts.OfType<ReasoningEndUIPart>());
        Assert.Single(uiParts.OfType<ToolCallStreamingStartPart>());
        Assert.Equal(8, uiParts.OfType<ToolCallDeltaPart>().Count());
        Assert.Single(uiParts.OfType<ToolCallPart>());
        Assert.Single(uiParts.OfType<ToolOutputAvailablePart>());
        Assert.Equal(9, uiParts.OfType<SourceUIPart>().Count());
        Assert.Single(uiParts.OfType<TextStartUIMessageStreamPart>());
        Assert.Single(uiParts.OfType<TextEndUIMessageStreamPart>());
        Assert.Single(uiParts.OfType<FinishUIPart>());

        var reasoningText = string.Concat(uiParts.OfType<ReasoningDeltaUIPart>().Select(part => part.Delta));
        Assert.Contains("latest news about war in Iran", reasoningText);
        Assert.Contains("Let me search for latest news about war in Iran.", reasoningText);

        var reasoningStartPart = Assert.IsType<ReasoningStartUIPart>(uiParts.Single(part => part.Type == "reasoning-start"));
        Assert.Null(reasoningStartPart.ProviderMetadata);

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(uiParts.Single(part => part.Type == "reasoning-end"));
        var reasoningProviderMetadata = Assert.Contains(ProviderId, reasoningEndPart.ProviderMetadata ?? []);
        Assert.Equal(originalSignature, Assert.IsType<string>(reasoningProviderMetadata["signature"]));

        var toolStartPart = Assert.IsType<ToolCallStreamingStartPart>(uiParts.Single(part => part.Type == "tool-input-start"));
        Assert.Equal(toolUseId, toolStartPart.ToolCallId);
        Assert.Equal("web_search", toolStartPart.ToolName);
        Assert.True(toolStartPart.ProviderExecuted);
        var toolStartProviderMetadata = Assert.Contains(ProviderId, toolStartPart.ProviderMetadata ?? []);
        Assert.Empty(toolStartProviderMetadata ?? []);

        var toolInputDeltaParts = uiParts.OfType<ToolCallDeltaPart>().ToList();
        Assert.All(toolInputDeltaParts, part => Assert.Equal(toolUseId, part.ToolCallId));
        Assert.Equal("{\"query\": \"latest news war Iran 2026\"}", string.Concat(toolInputDeltaParts.Select(part => part.InputTextDelta)));

        var toolCallPart = Assert.IsType<ToolCallPart>(uiParts.Single(part => part.Type == "tool-input-available"));
        Assert.Equal(toolUseId, toolCallPart.ToolCallId);
        Assert.Equal("web_search", toolCallPart.ToolName);
        Assert.True(toolCallPart.ProviderExecuted);
        var toolCallProviderMetadata = Assert.Contains(ProviderId, toolCallPart.ProviderMetadata ?? []);
        Assert.Empty(toolCallProviderMetadata ?? []);

        var toolInput = JsonSerializer.SerializeToElement(toolCallPart.Input, JsonSerializerOptions.Web);
        Assert.Equal("latest news war Iran 2026", toolInput.GetProperty("query").GetString());

        var toolOutputPart = Assert.IsType<ToolOutputAvailablePart>(uiParts.Single(part => part.Type == "tool-output-available"));
        Assert.Equal(toolUseId, toolOutputPart.ToolCallId);
        Assert.True(toolOutputPart.ProviderExecuted);
        var toolOutputProviderMetadata = Assert.Contains(ProviderId, toolOutputPart.ProviderMetadata ?? []);
        Assert.Equal(toolUseId, Assert.IsType<string>(toolOutputProviderMetadata?["tool_use_id"]));

        var toolOutput = JsonSerializer.SerializeToElement(toolOutputPart.Output, JsonSerializerOptions.Web);
        var searchResults = toolOutput.GetProperty("structuredContent").GetProperty("content");
        Assert.True(searchResults.GetArrayLength() >= 1);
        Assert.Equal("2026 Iran war - Wikipedia", searchResults[0].GetProperty("title").GetString());
        Assert.Equal("https://en.wikipedia.org/wiki/2026_Iran_war", searchResults[0].GetProperty("url").GetString());

        var sourceParts = uiParts.OfType<SourceUIPart>().ToList();
        Assert.Equal("https://en.wikipedia.org/wiki/2026_Iran_war", sourceParts[0].Url);
        Assert.Contains(sourceParts, part => part.Url == "https://www.aljazeera.com/news/liveblog/2026/4/16/iran-war-live-pakistan-in-push-for-new-round-of-us-iran-peace-negotiations");
        Assert.Contains(sourceParts, part => part.Url == "https://www.cnbc.com/2026/04/15/iran-war-trump-peace-deal-us-talks-stock-market-oil-prices-.html");

        var text = string.Concat(uiParts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta));
        Assert.StartsWith("## Samenvatting: Nieuwste ontwikkelingen in de Iraanse oorlog", text);
        Assert.Contains("Iran reageerde met raket- en dronenaanvallen", text);
        Assert.Contains("Het Internationaal Monetair Fonds heeft gewaarschuwd", text);

        var finishPart = Assert.IsType<FinishUIPart>(uiParts.Single(part => part.Type == "finish"));
        Assert.Equal("stop", finishPart.FinishReason);
        //Assert.Equal($"{ProviderId}/claude-haiku-4-5-20251001", finishPart.MessageMetadata?.Model);
        Assert.Equal(19246, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(789, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(20035, finishPart.MessageMetadata?.Usage.TotalTokens);

        var finishProviderMetadata = Assert.Contains(ProviderId, finishPart.MessageMetadata?.AdditionalProperties ?? []);
        var providerUsage = finishProviderMetadata.GetProperty("usage");
        Assert.Equal(19246, providerUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(789, providerUsage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(1, providerUsage.GetProperty("server_tool_use").GetProperty("web_search_requests").GetInt32());
    }

    [Fact]
    public void Provider_executed_tool_output_error_event_maps_to_ui_part_with_provider_metadata_key()
    {
        var envelope = new AIEventEnvelope
        {
            Type = "tool-output-error",
            Data = new AIToolOutputErrorEventData
            {
                ToolCallId = "tool-call-1",
                ErrorText = "provider tool failed",
                ProviderExecuted = true
            }
        };

        var part = Assert.IsType<ToolOutputErrorPart>(VercelUnifiedMapper.ToUIMessagePart(envelope, ProviderId).Single());

        Assert.Equal("tool-call-1", part.ToolCallId);
        Assert.True(part.ProviderExecuted);
        var providerMetadata = Assert.Contains(ProviderId, part.ProviderMetadata ?? []);
        Assert.Empty(providerMetadata ?? []);
    }

    [Fact]
    public void Client_executed_tool_input_start_event_does_not_get_synthetic_provider_metadata()
    {
        var envelope = new AIEventEnvelope
        {
            Type = "tool-input-start",
            Data = new AIToolInputStartEventData
            {
                ToolName = "client_tool",
                ProviderExecuted = false,
                Title = "Client tool"
            }
        };

        var part = Assert.IsType<ToolCallStreamingStartPart>(VercelUnifiedMapper.ToUIMessagePart(envelope, ProviderId).Single());

        Assert.False(part.ProviderExecuted);
        Assert.Null(part.ProviderMetadata);
    }

    [Fact]
    public async Task Github_chat_completions_fixture_with_trailing_usage_emits_single_terminal_finish_with_usage_metadata()
    {
        var unifiedEvents = await LoadChatCompletionUnifiedEventsAsync(
            GitHubRawFixturePath,
            GitHubProviderId,
            "github/Phi-4-mini-reasoning");

        Assert.Equal("finish", unifiedEvents[^1].Event.Type);

        var finishEvent = Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "finish");
        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);

        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(441, finishData.InputTokens);
        Assert.Equal(2652, finishData.OutputTokens);
        Assert.Equal(3093, finishData.TotalTokens);
        Assert.Equal(441, finishData.MessageMetadata?.Usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2652, finishData.MessageMetadata?.Usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(3093, finishData.MessageMetadata?.Usage.GetProperty("total_tokens").GetInt32());

        var uiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "text-start" or "text-delta" or "text-end" or "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(GitHubProviderId))
            .ToList();

        Assert.Equal("finish", uiParts[^1].Type);

        var finishPart = Assert.Single(uiParts.OfType<FinishUIPart>());
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(441, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(2652, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(3093, finishPart.MessageMetadata?.Usage.TotalTokens);
    }

    [Fact]
    public async Task Openai_chat_completions_fixture_with_usage_only_tail_emits_single_terminal_finish_with_usage_metadata()
    {
        var unifiedEvents = await LoadChatCompletionUnifiedEventsAsync(
            OpenAiWebSearchRawFixturePath,
            OpenAiProviderId,
            "gpt-5-search-api-2025-10-14");

        Assert.Equal("finish", unifiedEvents[^1].Event.Type);

        var finishEvent = Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "finish");
        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);

        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(16317, finishData.InputTokens);
        Assert.Equal(101, finishData.OutputTokens);
        Assert.Equal(16418, finishData.TotalTokens);
        Assert.Equal(1408, finishData.MessageMetadata?.Usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32());

        var finishPart = Assert.IsType<FinishUIPart>(finishEvent.Event.ToUIMessagePart(OpenAiProviderId).Single());
        Assert.Equal(16317, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(101, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(16418, finishPart.MessageMetadata?.Usage.TotalTokens);

        var providerMetadata = Assert.Contains(OpenAiProviderId, finishPart.MessageMetadata?.AdditionalProperties ?? []);
        var providerUsage = providerMetadata.GetProperty("usage");
        Assert.Equal(1408, providerUsage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32());
    }

    [Fact]
    public async Task Sonar_chat_completions_fixture_with_inline_finish_usage_emits_single_terminal_finish_with_preserved_provider_usage()
    {
        var unifiedEvents = await LoadChatCompletionUnifiedEventsAsync(
            SonarWebSearchRawFixturePath,
            PerplexityProviderId,
            "sonar");

        Assert.Equal("finish", unifiedEvents[^1].Event.Type);
        Assert.Single(unifiedEvents, streamEvent => streamEvent.Event.Type == "finish");

        var uiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is
                "reasoning-start" or
                "reasoning-delta" or
                "reasoning-end" or
                "tool-input-start" or
                "tool-input-available" or
                "tool-output-available" or
                "source-url" or
                "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(PerplexityProviderId))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(uiParts);
        Assert.Equal("finish", uiParts[^1].Type);

        var finishPart = Assert.Single(uiParts.OfType<FinishUIPart>());
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(431, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(260, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(691, finishPart.MessageMetadata?.Usage.TotalTokens);

        var providerMetadata = Assert.Contains(PerplexityProviderId, finishPart.MessageMetadata?.AdditionalProperties ?? []);
        var providerUsage = providerMetadata.GetProperty("usage");
        Assert.Equal("medium", providerUsage.GetProperty("search_context_size").GetString());
        Assert.Equal(0.00869m, providerUsage.GetProperty("cost").GetProperty("total_cost").GetDecimal());
        Assert.Equal(0.008m, providerUsage.GetProperty("cost").GetProperty("request_cost").GetDecimal());
    }

    [Fact]
    public async Task Groq_error_chat_completions_fixture_maps_to_unified_error_and_finish_events()
    {
        var provider = new FixtureChatCompletionStreamModelProvider(GroqProviderId, LoadChatCompletionRawFixture(GroqErrorRawFixturePath));
        var request = new AIRequest
        {
            ProviderId = GroqProviderId,
            Model = "openai/gpt-oss-20b",
            Stream = true
        };

        var unifiedEvents = await FixtureAssertions.CollectAsync(provider.StreamUnifiedViaChatCompletionsAsync(request));

        var terminalEvents = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "error" or "finish")
            .ToList();

        Assert.Collection(
            terminalEvents,
            streamEvent =>
            {
                Assert.Equal("error", streamEvent.Event.Type);
                var errorData = Assert.IsType<AIErrorEventData>(streamEvent.Event.Data);
                Assert.Contains("Tool choice is none, but model called a tool", errorData.ErrorText);
                Assert.Contains("invalid_request_error", errorData.ErrorText);
                Assert.Contains("tool_use_failed", errorData.ErrorText);
                Assert.Contains("browser.search", errorData.ErrorText);
            },
            streamEvent =>
            {
                Assert.Equal("finish", streamEvent.Event.Type);
                var finishData = Assert.IsType<AIFinishEventData>(streamEvent.Event.Data);
                Assert.Equal("error", finishData.FinishReason);
                Assert.Equal("openai/gpt-oss-20b", finishData.Model);
                Assert.Equal(1776280711L, Assert.IsType<long>(finishData.CompletedAt!));
            });
    }

    [Fact]
    public async Task Groq_error_chat_completions_fixture_maps_to_vercel_error_and_finish_ui_parts()
    {
        var provider = new FixtureChatCompletionStreamModelProvider(GroqProviderId, LoadChatCompletionRawFixture(GroqErrorRawFixturePath));
        var request = new AIRequest
        {
            ProviderId = GroqProviderId,
            Model = "openai/gpt-oss-20b",
            Stream = true
        };

        var unifiedEvents = await FixtureAssertions.CollectAsync(provider.StreamUnifiedViaChatCompletionsAsync(request));

        var uiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "error" or "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(GroqProviderId))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(uiParts);

        Assert.Collection(
            uiParts,
            uiPart =>
            {
                var errorPart = Assert.IsType<ErrorUIPart>(uiPart);
                Assert.Contains("Tool choice is none, but model called a tool", errorPart.ErrorText);
                Assert.Contains("invalid_request_error", errorPart.ErrorText);
                Assert.Contains("tool_use_failed", errorPart.ErrorText);
                Assert.Contains("browser.search", errorPart.ErrorText);
            },
            uiPart =>
            {
                var finishPart = Assert.IsType<FinishUIPart>(uiPart);
                Assert.Equal("error", finishPart.FinishReason);
                Assert.Equal("openai/gpt-oss-20b", finishPart.MessageMetadata?.Model);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1776280711), finishPart.MessageMetadata?.Timestamp);
            });
    }

    [Fact]
    public void Responses_shell_call_completed_response_recovery_maps_to_matching_final_tool_outputs_and_tool_inputs_in_ui_parts()
    {
        const string providerId = "openai";

        var parts = FixtureFileLoader.LoadResponseRawFixture(MultipleShellCallsWithStreamingOutputFixturePath)
            .Where(part => !IsLiveShellOutputStreamingPart(part))
            .ToList();

        var uiParts = parts
            .SelectMany(part => part.ToUnifiedStreamEvent(providerId))
            .Where(streamEvent => streamEvent.Event.Type is "tool-input-available" or "tool-output-available")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(providerId))
            .ToList();

        var toolInputParts = uiParts
            .OfType<ToolCallPart>()
            .Where(part => string.Equals(part.ToolName, "shell_call", StringComparison.Ordinal))
            .OrderBy(part => part.ToolCallId, StringComparer.Ordinal)
            .ToList();

        var finalToolOutputParts = uiParts
            .OfType<ToolOutputAvailablePart>()
            .Where(part => part.Preliminary is false or null)
            .Where(part => IsShellToolOutput(part, providerId))
            .OrderBy(part => part.ToolCallId, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(toolInputParts);
        Assert.All(toolInputParts, part => Assert.True(part.ProviderExecuted));
        Assert.All(finalToolOutputParts, part => Assert.True(part.ProviderExecuted));
        Assert.Equal(toolInputParts.Count, finalToolOutputParts.Count);
        Assert.Equal(
            toolInputParts.Select(part => part.ToolCallId).ToList(),
            finalToolOutputParts.Select(part => part.ToolCallId).ToList());
    }

    private static bool IsLiveShellOutputStreamingPart(ResponseStreamPart part)
        => part is ResponseOutputItemDone { Item.Type: "shell_call_output" }
            || part is ResponseUnknownEvent { Type: "response.shell_call_output_content.delta" or "response.shell_call_output_content.done" };

    private static bool IsShellToolOutput(ToolOutputAvailablePart part, string providerId)
        => part.ProviderMetadata?.TryGetValue(providerId, out var providerMetadata) == true
            && providerMetadata?.TryGetValue("tool_name", out var toolName) == true
            && string.Equals(toolName?.ToString(), "shell_call", StringComparison.Ordinal);

   /* [Fact]
    public void Sonar_web_search_chat_completions_fixture_maps_to_expected_ui_parts_and_finish_metadata()
    {
        var unifiedEvents = LoadChatCompletionUnifiedEvents(SonarWebSearchRawFixturePath, PerplexityProviderId);
        var eventTypes = unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList();

        FixtureAssertions.AssertContainsSubsequence(
            eventTypes,
            "reasoning-start",
            "reasoning-delta",
            "tool-input-start",
            "tool-input-available",
            "tool-output-available",
            "reasoning-end",
            "source-url",
            "finish");

        var uiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is
                "reasoning-start" or
                "reasoning-delta" or
                "reasoning-end" or
                "tool-input-start" or
                "tool-input-available" or
                "tool-output-available" or
                "source-url" or
                "tool-approval-request" or
                "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(PerplexityProviderId))
            .ToList();

        var finishPart = Assert.IsType<FinishUIPart>(uiParts.Last(part => part.Type == "finish"));
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal("perplexity/sonar", finishPart.MessageMetadata?.Model);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1776275349), finishPart.MessageMetadata?.Timestamp);
        Assert.Equal(431, finishPart.MessageMetadata?.InputTokens);
        Assert.Equal(260, finishPart.MessageMetadata?.OutputTokens);
        Assert.Equal(691, finishPart.MessageMetadata?.TotalTokens);
        var toolCallPart = Assert.IsType<ToolCallPart>(uiParts.Single(part => part.Type == "tool-input-available"));
        Assert.Equal("web_search", toolCallPart.ToolName);
        Assert.Equal("Web search", toolCallPart.Title);
        Assert.True(toolCallPart.ProviderExecuted);

        var toolInput = JsonSerializer.SerializeToElement(toolCallPart.Input, JsonSerializerOptions.Web);
        var searchKeywords = toolInput.GetProperty("search_keywords");
        Assert.Equal(JsonValueKind.Array, searchKeywords.ValueKind);
        Assert.Equal("latest news war Iran", searchKeywords[0].GetString());

        var toolOutputPart = Assert.IsType<ToolOutputAvailablePart>(uiParts.Single(part => part.Type == "tool-output-available"));
        Assert.True(toolOutputPart.ProviderExecuted);
        Assert.Null(toolOutputPart.Preliminary);

        var toolOutput = JsonSerializer.SerializeToElement(toolOutputPart.Output, JsonSerializerOptions.Web);
        var structuredContent = toolOutput.GetProperty("structuredContent");
        var searchResults = structuredContent.GetProperty("search_results");
        Assert.Equal(3, searchResults.GetArrayLength());
        Assert.Equal("Trump Admits Defeat After Iran Blockade Fails? Says 'War Is Over ...", searchResults[0].GetProperty("title").GetString());
        Assert.Equal("https://www.cbsnews.com/us-iran-tensions/", searchResults[2].GetProperty("url").GetString());

        var sourceParts = uiParts.OfType<SourceUIPart>().ToList();
        Assert.Equal(3, sourceParts.Count);
        Assert.Equal("https://www.youtube.com/watch?v=6dsqVAevsBk", sourceParts[0].Url);
        Assert.Equal("Trump Admits Defeat After Iran Blockade Fails? Says 'War Is Over ...", sourceParts[0].Title);
        Assert.Equal("https://www.cbsnews.com/us-iran-tensions/", sourceParts[2].Url);

        var sourceProviderMetadata = Assert.Contains(PerplexityProviderId, sourceParts[0].ProviderMetadata ?? new Dictionary<string, Dictionary<string, object>>());
        Assert.Equal("2026-04-15", Assert.IsType<string>(sourceProviderMetadata["date"]));
        Assert.Equal("web", Assert.IsType<string>(sourceProviderMetadata["source"]));

        Assert.DoesNotContain(uiParts, part => part is ToolApprovalRequestUIPart);
    }*/

    private static IReadOnlyList<ChatCompletionUpdate> LoadChatCompletionRawFixture(string relativePath)
    {
        var fullPath = FixtureFileLoader.ResolveFixturePath(relativePath);

        return [.. File.ReadLines(fullPath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["data:".Length..].Trim())
            .Where(line => line.Length > 0 && !string.Equals(line, "[DONE]", StringComparison.OrdinalIgnoreCase))
            .Select(payload => JsonSerializer.Deserialize<ChatCompletionUpdate>(payload, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException($"Could not deserialize chat completion stream payload from [{relativePath}](Core/AIHappey.Tests/{relativePath})."))];
    }

    private static async Task<List<AIStreamEvent>> LoadChatCompletionUnifiedEventsAsync(
        string relativePath,
        string providerId,
        string model)
    {
        var provider = new FixtureChatCompletionStreamModelProvider(providerId, LoadChatCompletionRawFixture(relativePath));
        var request = new AIRequest
        {
            ProviderId = providerId,
            Model = model,
            Stream = true
        };

        return await FixtureAssertions.CollectAsync(provider.StreamUnifiedViaChatCompletionsAsync(request));
    }
}
