using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Brave;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

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
    private const string BraveProviderId = "brave";

    [Fact]
    public void Download_file_tool_outputs_emit_file_ui_parts_when_payload_is_wrapped_under_structured_content()
    {
        var uiParts = Enumerable.Range(0, 2)
            .SelectMany(index => new AIEventEnvelope
            {
                Type = "tool-output-available",
                Id = $"download_{index + 1}",
                Data = new AIToolOutputAvailableEventData
                {
                    ToolName = "download_file",
                    ProviderExecuted = true,
                    Output = CreateWrappedDownloadPayload(
                        fileId: $"file_{index + 1}",
                        filename: index == 0 ? "random_data.txt" : "random_data.zip",
                        mediaType: index == 0 ? "text/plain" : "application/zip",
                        dataUrl: index == 0
                            ? "data:text/plain;base64,SGVsbG8="
                            : "data:application/zip;base64,UEsDBA=="),
                    ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                    {
                        [OpenAiProviderId] = new()
                        {
                            ["tool_name"] = "download_file",
                            ["download_tool"] = true
                        }
                    }
                }
            }.ToUIMessagePart(OpenAiProviderId))
            .ToList();

        Assert.Equal(2, uiParts.OfType<ToolOutputAvailablePart>().Count());

        var fileParts = uiParts.OfType<FileUIPart>().ToList();
        Assert.Equal(2, fileParts.Count);

        Assert.Collection(
            fileParts,
            filePart =>
            {
                Assert.Equal("text/plain", filePart.MediaType);
                Assert.Equal("data:text/plain;base64,SGVsbG8=", filePart.Url);
            },
            filePart =>
            {
                Assert.Equal("application/zip", filePart.MediaType);
                Assert.Equal("data:application/zip;base64,UEsDBA==", filePart.Url);
            });
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
    public async Task Brave_usage_tag_is_removed_from_text_and_forwarded_as_gateway_cost()
    {
        var parts = CreateBraveUsageTagFixture();
        var filteredParts = new List<ChatCompletionUpdate>();

        decimal? capturedCost = null;
        foreach (var part in parts)
        {
            if (BraveProvider.TryCaptureUsageCost(part, out var cost))
            {
                capturedCost = cost;
                continue;
            }

            if (capturedCost is not null)
                ApplyGatewayCostForTest(part, capturedCost.Value);

            filteredParts.Add(part);
        }

        var provider = new FixtureChatCompletionStreamModelProvider(BraveProviderId, filteredParts);
        var request = new AIRequest
        {
            ProviderId = BraveProviderId,
            Model = "brave-pro",
            Stream = true
        };

        var unifiedEvents = await FixtureAssertions.CollectAsync(provider.StreamUnifiedViaChatCompletionsAsync(request));
        var text = string.Concat(unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "text-delta")
            .Select(streamEvent => Assert.IsType<AITextDeltaEventData>(streamEvent.Event.Data).Delta));

        Assert.Equal("gh", text);
        Assert.DoesNotContain("<usage>", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Request-Total-Cost", text, StringComparison.OrdinalIgnoreCase);

        var finishData = Assert.IsType<AIFinishEventData>(unifiedEvents.Single(streamEvent => streamEvent.Event.Type == "finish").Event.Data);
        Assert.Equal(8290, finishData.InputTokens);
        Assert.Equal(316, finishData.OutputTokens);
        Assert.Equal(8606, finishData.TotalTokens);
        Assert.Equal(0.04703m, finishData.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public async Task Brave_citation_tags_are_removed_from_text_and_emitted_as_source_url_ui_parts()
    {
        var provider = new TestBraveProvider(CreateBraveCitationTagFixture());
        var request = new ChatRequest
        {
            Model = "brave-pro"
        };

        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(request));

        var text = string.Concat(uiParts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta));
        Assert.Equal("Before  after", text);
        Assert.DoesNotContain("<citation>", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</citation>", text, StringComparison.OrdinalIgnoreCase);

        var sourcePart = Assert.Single(uiParts.OfType<SourceUIPart>());
        Assert.Equal("brave-citation-1", sourcePart.SourceId);
        Assert.Equal("https://nos.nl/liveblog/2612248-iran-stuurt-nieuw-vredesvoorstel-naar-vs-witte-huis-negeert-deadline-congres", sourcePart.Url);
        Assert.Contains("Midden-Oosten", sourcePart.Title);

        var braveMetadata = Assert.Contains(BraveProviderId, sourcePart.ProviderMetadata ?? []);
        Assert.Equal(1, Assert.IsType<int>(braveMetadata["number"]));
        Assert.Equal(186, Assert.IsType<int>(braveMetadata["start_index"]));
        Assert.Equal(189, Assert.IsType<int>(braveMetadata["end_index"]));
        Assert.Equal("Midden-Oosten NOS Nieuws", Assert.IsType<string>(braveMetadata["snippet"]));
        Assert.Equal("https://imgs.search.brave.com/favicon", Assert.IsType<string>(braveMetadata["favicon"]));
        Assert.IsType<JsonElement>(braveMetadata["raw"]);

        Assert.Single(uiParts.OfType<FinishUIPart>());
    }

    [Fact]
    public async Task Brave_enum_items_emit_original_tokens_entity_sources_citation_sources_and_downloaded_image_file_parts()
    {
        const string imageUrl = "https://imgs.search.brave.com/test-cover.png";
        var imageBytes = Encoding.UTF8.GetBytes("brave-image-bytes");
        var imageDownloads = 0;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == imageUrl)
            {
                imageDownloads++;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes)
                };

                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var provider = new TestBraveProvider(
            CreateBraveEnumItemFixture(imageUrl),
            new StaticHttpClientFactory(new HttpClient(handler)));
        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(new ChatRequest { Model = "brave-pro" }));

        var text = string.Concat(uiParts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta));
        Assert.Equal("Intro * **The Fame (2008)**: Debuutalbum[1].\n outro", text);
        Assert.DoesNotContain("<enum_start>", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<enum_item>", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<enum_end>", text, StringComparison.OrdinalIgnoreCase);

        var sourceParts = uiParts.OfType<SourceUIPart>().ToList();
        Assert.Equal(3, sourceParts.Count);
        Assert.Equal(1, sourceParts.Count(part => part.SourceId == "brave-entity-entity-1"));
        Assert.Equal(1, sourceParts.Count(part => part.SourceId == "brave-citation-1"));
        Assert.Equal(1, sourceParts.Count(part => part.SourceId == "brave-citation-2"));

        var entitySource = sourceParts.Single(part => part.SourceId == "brave-entity-entity-1");
        Assert.Equal("https://en.wikipedia.org/wiki/The_Fame", entitySource.Url);
        Assert.Equal("The Fame (2008)", entitySource.Title);
        var entityMetadata = Assert.Contains(BraveProviderId, entitySource.ProviderMetadata ?? []);
        Assert.Equal("entity", Assert.IsType<string>(entityMetadata["kind"]));
        Assert.Equal("entity-1", Assert.IsType<string>(entityMetadata["uuid"]));
        Assert.Equal("* **The Fame (2008)**: Debuutalbum[1].\n", Assert.IsType<string>(entityMetadata["original_tokens"]));
        Assert.IsType<JsonElement>(entityMetadata["images"]);

        var citationSource = sourceParts.Single(part => part.SourceId == "brave-citation-1");
        Assert.Equal("https://albums.example/the-fame", citationSource.Url);
        Assert.Equal("Albums source", citationSource.Title);
        var citationMetadata = Assert.Contains(BraveProviderId, citationSource.ProviderMetadata ?? []);
        Assert.Equal(1, Assert.IsType<int>(citationMetadata["number"]));

        var filePart = Assert.Single(uiParts.OfType<FileUIPart>());
        Assert.Equal("image/png", filePart.MediaType);
        Assert.Equal(Convert.ToBase64String(imageBytes), filePart.Url);
        Assert.Equal("test-cover.png", filePart.Filename);
        var fileMetadata = Assert.Contains(BraveProviderId, filePart.ProviderMetadata ?? []);
        Assert.NotNull(fileMetadata);
        Assert.Equal("entity_image", Assert.IsType<string>(fileMetadata!["kind"]));
        Assert.Equal(imageUrl, Assert.IsType<string>(fileMetadata["origin_url"]));
        Assert.Equal(1, imageDownloads);
    }

    [Fact]
    public async Task Brave_enum_items_without_href_skip_entity_source_but_keep_original_tokens_and_citations()
    {
        var provider = new TestBraveProvider(CreateBraveEnumItemWithoutHrefFixture());
        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(new ChatRequest { Model = "brave-pro" }));

        var text = string.Concat(uiParts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta));
        Assert.Equal("* **MAYHEM (Reissue) (2025)**: Heruitgave[4].\n", text);

        var sourcePart = Assert.Single(uiParts.OfType<SourceUIPart>());
        Assert.Equal("brave-citation-4", sourcePart.SourceId);
        Assert.Equal("https://genius.com/artists/Lady-gaga/albums", sourcePart.Url);
        Assert.Empty(uiParts.OfType<FileUIPart>());
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

    private static JsonElement CreateWrappedDownloadPayload(
        string fileId,
        string filename,
        string mediaType,
        string dataUrl)
        => JsonSerializer.SerializeToElement(new
        {
            structuredContent = new
            {
                file_id = fileId,
                filename,
                media_type = mediaType,
                url = $"https://api.openai.com/v1/files/{fileId}/content",
                data_url = dataUrl
            },
            content = new
            {
                unsupported = true,
                kind = "blob-resource"
            }
        }, JsonSerializerOptions.Web);

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

    private static IReadOnlyList<ChatCompletionUpdate> CreateBraveUsageTagFixture()
        =>
        [
            DeserializeChatCompletionUpdate("""
            {"model":"brave-pro","system_fingerprint":"","choices":[{"delta":{"role":"assistant","content":"g"},"finish_reason":null}],"created":1777966764,"id":"15c21ebe-01f6-4050-891e-413186385f31","object":"chat.completion.chunk","usage":null}
            """),
            DeserializeChatCompletionUpdate("""
            {"model":"brave-pro","system_fingerprint":"","choices":[{"delta":{"role":"assistant","content":"h"},"finish_reason":null}],"created":1777966764,"id":"15c21ebe-01f6-4050-891e-413186385f31","object":"chat.completion.chunk","usage":null}
            """),
            DeserializeChatCompletionUpdate("""
            {"model":"brave-pro","system_fingerprint":"","choices":[{"delta":{"role":"assistant","content":"<usage>{\"X-Request-Requests\": 1, \"X-Request-Queries\": 1, \"X-Request-Tokens-In\": 8290, \"X-Request-Tokens-Out\": 316, \"X-Request-Requests-Cost\": 0.0, \"X-Request-Queries-Cost\": 0.004, \"X-Request-Tokens-In-Cost\": 0.04145, \"X-Request-Tokens-Out-Cost\": 0.00158, \"X-Request-Total-Cost\": 0.04703}</usage>"},"finish_reason":null}],"created":1777966764,"id":"15c21ebe-01f6-4050-891e-413186385f31","object":"chat.completion.chunk","usage":null}
            """),
            DeserializeChatCompletionUpdate("""
            {"model":"brave-pro","system_fingerprint":"","choices":[{"delta":{"role":"assistant","content":""},"finish_reason":"stop"}],"created":1777966764,"id":"15c21ebe-01f6-4050-891e-413186385f31","object":"chat.completion.chunk","usage":{"completion_tokens":316,"prompt_tokens":8290,"total_tokens":8606,"completion_tokens_details":{"reasoning_tokens":0}}}
            """)
        ];

    private sealed class TestBraveProvider : BraveProvider
    {
        private readonly IReadOnlyList<ChatCompletionUpdate> chatCompletionUpdates;

        public TestBraveProvider(IReadOnlyList<ChatCompletionUpdate> chatCompletionUpdates)
            : this(chatCompletionUpdates, new TestHttpClientFactory())
        {
        }

        public TestBraveProvider(IReadOnlyList<ChatCompletionUpdate> chatCompletionUpdates, IHttpClientFactory httpClientFactory)
            : base(new NullApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), httpClientFactory)
            => this.chatCompletionUpdates = chatCompletionUpdates;

        public override IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
            ChatCompletionOptions options,
            CancellationToken cancellationToken = default)
            => ReplayAsync(cancellationToken);

        private async IAsyncEnumerable<ChatCompletionUpdate> ReplayAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in chatCompletionUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }
    }

    private sealed class NullApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static IReadOnlyList<ChatCompletionUpdate> CreateBraveCitationTagFixture()
    {
        const string citation = """
        <citation>{"start_index":186,"end_index":189,"number":1,"url":"https://nos.nl/liveblog/2612248-iran-stuurt-nieuw-vredesvoorstel-naar-vs-witte-huis-negeert-deadline-congres","favicon":"https://imgs.search.brave.com/favicon","snippet":"Midden-Oosten NOS Nieuws"}</citation>
        """;

        var chunks = new[] { "Before ", citation, " after" };

        var updates = chunks
            .Select(content => CreateBraveTextChunk(content))
            .ToList();

        updates.Add(DeserializeChatCompletionUpdate("""
        {"model":"brave-pro","system_fingerprint":"","choices":[{"delta":{"role":"assistant","content":""},"finish_reason":"stop"}],"created":1777969142,"id":"efdfe159-2cca-47c6-b853-0e8466b7656d","object":"chat.completion.chunk","usage":{"completion_tokens":12,"prompt_tokens":34,"total_tokens":46}}
        """));

        return updates;
    }

    private static IReadOnlyList<ChatCompletionUpdate> CreateBraveEnumItemFixture(string imageUrl)
    {
        var entity = new Dictionary<string, object?>
        {
            ["uuid"] = "entity-1",
            ["name"] = "The Fame (2008)",
            ["href"] = "https://en.wikipedia.org/wiki/The_Fame",
            ["extra_text"] = "Debuutalbum",
            ["original_tokens"] = "* **The Fame (2008)**: Debuutalbum[1].\n",
            ["instance_of"] = new[] { "Q482994" },
            ["images"] = new[] { imageUrl, imageUrl },
            ["citations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["start_index"] = 10,
                    ["end_index"] = 13,
                    ["number"] = 1,
                    ["url"] = "https://albums.example/the-fame",
                    ["favicon"] = "https://imgs.search.brave.com/favicon-1",
                    ["snippet"] = "Albums source"
                },
                new Dictionary<string, object?>
                {
                    ["start_index"] = 10,
                    ["end_index"] = 13,
                    ["number"] = 1,
                    ["url"] = "https://albums.example/the-fame",
                    ["favicon"] = "https://imgs.search.brave.com/favicon-1",
                    ["snippet"] = "Albums source"
                },
                new Dictionary<string, object?>
                {
                    ["start_index"] = 13,
                    ["end_index"] = 16,
                    ["number"] = 2,
                    ["url"] = "https://grokipedia.example/lady-gaga-discography",
                    ["favicon"] = "https://imgs.search.brave.com/favicon-2",
                    ["snippet"] = "Grokipedia source"
                }
            }
        };

        var chunks = new[]
        {
            "Intro ",
            "<enum_start>{\"type\":\"ul\"}</enum_start>",
            $"<enum_item>{JsonSerializer.Serialize(entity, JsonSerializerOptions.Web)}</enum_item>",
            "<enum_end>{}</enum_end>",
            " outro"
        };

        var updates = chunks
            .Select(CreateBraveTextChunk)
            .ToList();

        updates.Add(DeserializeChatCompletionUpdate("""
        {"model":"brave-pro","system_fingerprint":"","choices":[{"delta":{"role":"assistant","content":""},"finish_reason":"stop"}],"created":1777969142,"id":"efdfe159-2cca-47c6-b853-0e8466b7656d","object":"chat.completion.chunk","usage":{"completion_tokens":12,"prompt_tokens":34,"total_tokens":46}}
        """));

        return updates;
    }

    private static IReadOnlyList<ChatCompletionUpdate> CreateBraveEnumItemWithoutHrefFixture()
    {
        var entity = new Dictionary<string, object?>
        {
            ["uuid"] = "entity-no-href",
            ["name"] = "MAYHEM (Reissue) (2025)",
            ["href"] = null,
            ["extra_text"] = "Heruitgave",
            ["original_tokens"] = "* **MAYHEM (Reissue) (2025)**: Heruitgave[4].\n",
            ["instance_of"] = Array.Empty<string>(),
            ["images"] = Array.Empty<string>(),
            ["citations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["start_index"] = 20,
                    ["end_index"] = 23,
                    ["number"] = 4,
                    ["url"] = "https://genius.com/artists/Lady-gaga/albums",
                    ["favicon"] = "https://imgs.search.brave.com/genius-favicon",
                    ["snippet"] = "Lady Gaga Albums"
                }
            }
        };

        var updates = new[]
        {
            CreateBraveTextChunk($"<enum_item>{JsonSerializer.Serialize(entity, JsonSerializerOptions.Web)}</enum_item>"),
            DeserializeChatCompletionUpdate("""
            {"model":"brave-pro","system_fingerprint":"","choices":[{"delta":{"role":"assistant","content":""},"finish_reason":"stop"}],"created":1777969142,"id":"efdfe159-2cca-47c6-b853-0e8466b7656d","object":"chat.completion.chunk","usage":{"completion_tokens":12,"prompt_tokens":34,"total_tokens":46}}
            """)
        };

        return updates;
    }

    private static ChatCompletionUpdate CreateBraveTextChunk(string content)
        => new()
        {
            Id = "efdfe159-2cca-47c6-b853-0e8466b7656d",
            Object = "chat.completion.chunk",
            Created = 1777969142,
            Model = "brave-pro",
            Choices =
            [
                JsonSerializer.SerializeToElement(new
                {
                    delta = new
                    {
                        role = "assistant",
                        content
                    },
                    finish_reason = (string?)null
                }, JsonSerializerOptions.Web)
            ]
        };

    private static ChatCompletionUpdate DeserializeChatCompletionUpdate(string json)
        => JsonSerializer.Deserialize<ChatCompletionUpdate>(json, JsonSerializerOptions.Web)
            ?? throw new JsonException("Could not deserialize chat completion update fixture.");

    private static void ApplyGatewayCostForTest(ChatCompletionUpdate update, decimal cost)
    {
        var json = JsonSerializer.SerializeToElement(update, JsonSerializerOptions.Web);
        var metadata = json.TryGetProperty("metadata", out var existingMetadata) && existingMetadata.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(existingMetadata.GetRawText(), JsonSerializerOptions.Web) ?? []
            : [];

        metadata["gateway"] = new Dictionary<string, object?>
        {
            ["cost"] = cost
        };

        update.AdditionalProperties ??= [];
        update.AdditionalProperties["metadata"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
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
