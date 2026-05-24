using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Zai;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Zai;

public sealed class ZaiProviderAgentsTests
{
    private const string ProviderId = "zai";
    private const string ComplexSlideAgentStreamFixturePath = "Fixtures/zai/raw/complex-zai-glm-slide-poster-agent-stream.jsonl";

    [Fact]
    public async Task CompleteChatAsync_routes_agent_model_to_agents_endpoint_and_forwards_custom_variables()
    {
        HttpRequestMessage? captured = null;
        string? body = null;

        var provider = CreateProvider(request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse(new
            {
                id = "agent-run-1",
                agent_id = "general_translation",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        finish_reason = "stop",
                        messages = new
                        {
                            role = "assistant",
                            content = new { type = "text", text = "Hallo wereld" }
                        }
                    }
                },
                usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 }
            });
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "zai/agents/general_translation",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello world")
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["zai"] = JsonSerializer.SerializeToElement(new
                {
                    custom_variables = new
                    {
                        source_lang = "en",
                        target_lang = "nl",
                        strategy = "general"
                    },
                    request_id = "req-123"
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.Equal("/api/v1/agents", captured!.RequestUri!.PathAndQuery);
        Assert.Contains("Bearer", captured.Headers.Authorization!.Scheme);

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        Assert.Equal("general_translation", root.GetProperty("agent_id").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("req-123", root.GetProperty("request_id").GetString());
        Assert.Equal("nl", root.GetProperty("custom_variables").GetProperty("target_lang").GetString());
        Assert.Equal("Hello world", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());

        Assert.Equal("agent-run-1", response.Id);
        var choice = Assert.Single(response.Choices);
        var choiceJson = JsonSerializer.SerializeToElement(choice, JsonSerializerOptions.Web);
        Assert.Equal("Hallo wereld", choiceJson.GetProperty("message").GetProperty("content").GetString());
        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.000015m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task CompleteChatAsync_maps_image_url_content_for_vidu_template_agent()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                status = "pending",
                agent_id = "vidu_template_agent",
                async_id = "async-123"
            });
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "zai/agents/vidu_template_agent",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(new object[]
                    {
                        new { type = "text", text = "make it dance" },
                        new { type = "image_url", image_url = new { url = "https://example.com/image.png" } }
                    }, JsonSerializerOptions.Web)
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["zai"] = JsonSerializer.SerializeToElement(new
                {
                    custom_variables = new { template = "bodyshake" }
                }, JsonSerializerOptions.Web)
            }
        });

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        Assert.Equal("vidu_template_agent", root.GetProperty("agent_id").GetString());
        Assert.False(root.TryGetProperty("stream", out _));
        Assert.Equal("bodyshake", root.GetProperty("custom_variables").GetProperty("template").GetString());
        Assert.Equal("https://example.com/image.png", root.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetString());

        var choice = Assert.Single(response.Choices);
        var choiceJson = JsonSerializer.SerializeToElement(choice, JsonSerializerOptions.Web);
        Assert.Contains("async-123", choiceJson.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteChatAsync_extracts_slide_text_and_tool_output()
    {
        var provider = CreateProvider(_ => JsonResponse(new
        {
            id = "slide-run-1",
            conversation_id = "conversation-1",
            agent_id = "slides_glm_agent",
            choices = new[]
            {
                new
                {
                    index = 0,
                    finish_reason = "stop",
                    message = new[]
                    {
                        new
                        {
                            role = "assistant",
                            phase = "answer",
                            content = new object[]
                            {
                                new { type = "text", text = "Here is the deck." },
                                new { type = "object", @object = new { tool_name = "insert_page", output = "<html>slide</html>" } }
                            }
                        }
                    }
                }
            }
        }));

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "zai/agents/slides_glm_agent",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Create a sales deck")
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["zai"] = JsonSerializer.SerializeToElement(new { conversation_id = "conversation-1" }, JsonSerializerOptions.Web)
            }
        });

        var choice = Assert.Single(response.Choices);
        var choiceJson = JsonSerializer.SerializeToElement(choice, JsonSerializerOptions.Web);
        var content = choiceJson.GetProperty("message").GetProperty("content").GetString();
        Assert.Contains("Here is the deck.", content);
        Assert.Contains("<html>slide</html>", content);
        Assert.True(response.AdditionalProperties!.ContainsKey("zai_agent"));
    }

    [Fact]
    public async Task CompleteChatAsync_captures_agent_json_response_when_capture_metadata_is_present()
    {
        var captureRoot = CreateTempCaptureRoot();
        var previousCaptureOptions = ProviderBackendCapture.Current;

        try
        {
            ProviderBackendCapture.Configure(new ProviderBackendCaptureOptions
            {
                Enabled = true,
                DevelopmentOnly = false,
                RootDirectory = captureRoot
            });

            var provider = CreateProvider(_ => JsonResponse(new
            {
                id = "agent-capture-1",
                agent_id = "general_translation",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        finish_reason = "stop",
                        messages = new
                        {
                            role = "assistant",
                            content = new { type = "text", text = "Hallo capture" }
                        }
                    }
                }
            }));

            _ = await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = "zai/agents/general_translation",
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "user",
                        Content = JsonSerializer.SerializeToElement("Capture this response")
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["zai"] = JsonSerializer.SerializeToElement(new
                    {
                        capture = new
                        {
                            relativeDirectory = "zai-agent-capture",
                            fileName = "translation-response"
                        }
                    }, JsonSerializerOptions.Web)
                }
            });

            var captureFiles = Directory.GetFiles(captureRoot, "*", SearchOption.AllDirectories);
            var captureFile = Assert.Single(captureFiles);
            Assert.EndsWith(Path.Combine("zai-agent-capture", "translation-response.json"), captureFile);

            var captured = await File.ReadAllTextAsync(captureFile);
            using var doc = JsonDocument.Parse(captured);
            Assert.Equal("agent-capture-1", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal("general_translation", doc.RootElement.GetProperty("agent_id").GetString());
        }
        finally
        {
            ProviderBackendCapture.Configure(previousCaptureOptions);
            TryDeleteDirectory(captureRoot);
        }
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_maps_agent_sse_to_chat_completion_updates()
    {
        var provider = CreateProvider(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"stream\":true", body);

            return StreamingResponse("""
                data: {"id":"stream-1","agent_id":"general_translation","choices":[{"index":0,"messages":{"role":"assistant","content":{"type":"text","text":"Hallo"}}}]}

                data: [DONE]

                """);
        });

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "zai/agents/general_translation",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello")
                }
            ]
        }))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        var firstChoice = JsonSerializer.SerializeToElement(Assert.Single(updates[0].Choices), JsonSerializerOptions.Web);
        Assert.Equal("Hallo", firstChoice.GetProperty("delta").GetProperty("content").GetString());
        var finalChoice = JsonSerializer.SerializeToElement(Assert.Single(updates[1].Choices), JsonSerializerOptions.Web);
        Assert.Equal("stop", finalChoice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_enriches_terminal_agent_usage_with_gateway_cost()
    {
        var provider = CreateProvider(_ => StreamingResponse("""
            data: {"id":"slide-stream-usage","agent_id":"slides_glm_agent","conversation_id":"conversation-1","choices":[{"index":0,"messages":[{"role":"assistant","content":{"type":"text","text":"🌭"},"phase":"answer"}]}]}

            data: {"id":"slide-stream-usage","agent_id":"slides_glm_agent","conversation_id":"conversation-1","choices":[{"finish_reason":"stop"}],"usage":{"prompt_tokens":88197,"completion_tokens":6051,"total_tokens":94248}}

            data: [DONE]

            """));

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "zai/agents/slides_glm_agent",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Create slides")
                }
            ]
        }))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.False(updates[0].AdditionalProperties?.ContainsKey("metadata") == true);

        var gateway = updates[1].AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.0659736m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_captures_raw_agent_sse_when_backend_capture_metadata_is_present()
    {
        var captureRoot = CreateTempCaptureRoot();
        var previousCaptureOptions = ProviderBackendCapture.Current;

        try
        {
            ProviderBackendCapture.Configure(new ProviderBackendCaptureOptions
            {
                Enabled = true,
                DevelopmentOnly = false,
                RootDirectory = captureRoot
            });

            var provider = CreateProvider(_ => StreamingResponse("""
                data: {"id":"stream-capture-1","agent_id":"general_translation","choices":[{"index":0,"messages":{"role":"assistant","content":{"type":"text","text":"Hallo"}}}]}

                data: [DONE]

                """));

            var updates = new List<ChatCompletionUpdate>();
            await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
            {
                Model = "zai/agents/general_translation",
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "user",
                        Content = JsonSerializer.SerializeToElement("Capture this stream")
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["zai"] = JsonSerializer.SerializeToElement(new
                    {
                        backend_capture = new
                        {
                            relativeDirectory = "zai-agent-stream-capture",
                            fileName = "translation-stream"
                        }
                    }, JsonSerializerOptions.Web)
                }
            }))
            {
                updates.Add(update);
            }

            Assert.Equal(2, updates.Count);

            var captureFiles = Directory.GetFiles(captureRoot, "*", SearchOption.AllDirectories);
            var captureFile = Assert.Single(captureFiles);
            Assert.EndsWith(Path.Combine("zai-agent-stream-capture", "translation-stream.jsonl"), captureFile);

            var captured = await File.ReadAllTextAsync(captureFile);
            Assert.Contains("data:", captured);
            Assert.Contains("stream-capture-1", captured);
            Assert.Contains("Hallo", captured);
            Assert.Contains("data: [DONE]", captured);
        }
        finally
        {
            ProviderBackendCapture.Configure(previousCaptureOptions);
            TryDeleteDirectory(captureRoot);
        }
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_does_not_mark_each_text_delta_as_finished()
    {
        var provider = CreateProvider(_ => StreamingResponse("""
            data: {"id":"stream-2","agent_id":"general_translation","choices":[{"index":0,"messages":{"role":"assistant","content":{"type":"text","text":"Hal"}}}]}

            data: {"id":"stream-2","agent_id":"general_translation","choices":[{"index":0,"messages":{"role":"assistant","content":{"type":"text","text":"lo"}}}]}

            data: [DONE]

            """));

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "zai/agents/general_translation",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello")
                }
            ]
        }))
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);

        var firstChoice = JsonSerializer.SerializeToElement(Assert.Single(updates[0].Choices), JsonSerializerOptions.Web);
        Assert.Equal("Hal", firstChoice.GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, firstChoice.GetProperty("finish_reason").ValueKind);

        var secondChoice = JsonSerializer.SerializeToElement(Assert.Single(updates[1].Choices), JsonSerializerOptions.Web);
        Assert.Equal("lo", secondChoice.GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, secondChoice.GetProperty("finish_reason").ValueKind);

        var finalChoice = JsonSerializer.SerializeToElement(Assert.Single(updates[2].Choices), JsonSerializerOptions.Web);
        Assert.Equal("stop", finalChoice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_does_not_emit_duplicate_tail_finish_when_provider_sends_terminal_chunk()
    {
        var provider = CreateProvider(_ => StreamingResponse("""
            data: {"id":"stream-3","agent_id":"general_translation","choices":[{"index":0,"messages":{"role":"assistant","content":{"type":"text","text":"Hallo"}}}]}

            data: {"id":"stream-3","agent_id":"general_translation","choices":[{"index":0,"finish_reason":"stop"}]}

            data: [DONE]

            """));

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "zai/agents/general_translation",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello")
                }
            ]
        }))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);

        var firstChoice = JsonSerializer.SerializeToElement(Assert.Single(updates[0].Choices), JsonSerializerOptions.Web);
        Assert.Equal("Hallo", firstChoice.GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, firstChoice.GetProperty("finish_reason").ValueKind);

        var finalChoice = JsonSerializer.SerializeToElement(Assert.Single(updates[1].Choices), JsonSerializerOptions.Web);
        Assert.Equal("stop", finalChoice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_rejects_vidu_template_agent()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
            {
                Model = "zai/agents/vidu_template_agent",
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "user",
                        Content = JsonSerializer.SerializeToElement("animate")
                    }
                ]
            }))
            {
            }
        });

        Assert.Contains("vidu_template_agent", ex.Message);
    }

    [Fact]
    public async Task StreamUnifiedAsync_for_complex_slide_fixture_emits_final_provider_executed_tool_output_and_embedded_html_file()
    {
        var fixture = FixtureFileLoader.ResolveFixturePath(ComplexSlideAgentStreamFixturePath);
        var provider = CreateProvider(_ => StreamingResponse(File.ReadAllText(fixture)));

        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = ProviderId,
            Id = "zai-test-run",
            Model = "zai/agents/slides_glm_agent",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Maak een buurtbarbecueposter."
                            }
                        ]
                    }
                ]
            }
        }));

        var toolOutputEvents = events.Where(streamEvent =>
            string.Equals(streamEvent.Event.Type, "tool-output-available", StringComparison.Ordinal)
            && streamEvent.Event.Data is AIToolOutputAvailableEventData
            {
                ToolName: "add_slide",
                ProviderExecuted: true
            }).ToList();
        Assert.NotEmpty(toolOutputEvents);

        var finalToolOutputEvent = toolOutputEvents[^1];

        var finalToolOutput = Assert.IsType<AIToolOutputAvailableEventData>(finalToolOutputEvent.Event.Data);

        var toolResultJson = JsonSerializer.SerializeToElement(finalToolOutput.Output, JsonSerializerOptions.Web);
        var structuredContent = toolResultJson.GetProperty("structuredContent");
        Assert.Equal("text/html", structuredContent.GetProperty("media_type").GetString());
        Assert.StartsWith("data:text/html", structuredContent.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);

        var htmlFromStructuredContent = structuredContent.GetProperty("html").GetString();
        Assert.NotNull(htmlFromStructuredContent);
        Assert.Contains("<html", htmlFromStructuredContent!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", htmlFromStructuredContent!, StringComparison.OrdinalIgnoreCase);

        var fileEvents = events.Where(streamEvent =>
            string.Equals(streamEvent.Event.Type, "file", StringComparison.Ordinal)
            && string.Equals(streamEvent.Event.Id, finalToolOutputEvent.Event.Id, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(fileEvents);

        var fileEvent = fileEvents[^1];

        var fileData = Assert.IsType<AIFileEventData>(fileEvent.Event.Data);
        Assert.Equal("text/html", fileData.MediaType);
        Assert.StartsWith("data:text/html", fileData.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zai://", fileData.Url, StringComparison.OrdinalIgnoreCase);

        var decodedHtml = DecodeDataUrl(fileData.Url);
        Assert.Contains("<html", decodedHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", decodedHtml, StringComparison.OrdinalIgnoreCase);

        var uiParts = events
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        var outputPart = uiParts.OfType<ToolOutputAvailablePart>().Last(part =>
            string.Equals(part.ToolCallId, finalToolOutputEvent.Event.Id, StringComparison.Ordinal)
            && part.ProviderExecuted == true);
        Assert.True(outputPart.ProviderExecuted);

        var filePart = uiParts.OfType<FileUIPart>().Last();
        Assert.Equal("text/html", filePart.MediaType);
        Assert.StartsWith("data:text/html", filePart.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zai://", filePart.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamUnifiedAsync_emits_tool_input_available_before_tool_output_available_for_provider_executed_slide_tools()
    {
        var provider = CreateProvider(_ => StreamingResponse("""
            data: {"id":"slide-order-1","agent_id":"slides_glm_agent","conversation_id":"conversation-1","choices":[{"index":0,"messages":[{"role":"assistant","phase":"tool","content":[{"type":"object","object":{"tool_name":"search","input":"[\"poster\"]","output":[]},"tag_en":"Search"}]}]}]}

            data: {"id":"slide-order-1","agent_id":"slides_glm_agent","conversation_id":"conversation-1","choices":[{"index":0,"finish_reason":"stop"}]}

            data: [DONE]

            """));

        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = ProviderId,
            Id = "zai-tool-order-run",
            Model = "zai/agents/slides_glm_agent",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Maak een poster."
                            }
                        ]
                    }
                ]
            }
        }));

        var toolInputEvent = Assert.Single(events, streamEvent =>
            string.Equals(streamEvent.Event.Type, "tool-input-available", StringComparison.Ordinal)
            && streamEvent.Event.Data is AIToolInputAvailableEventData { ToolName: "search", ProviderExecuted: true });

        var toolOutputEvents = events.Where(streamEvent =>
            string.Equals(streamEvent.Event.Type, "tool-output-available", StringComparison.Ordinal)
            && string.Equals(streamEvent.Event.Id, toolInputEvent.Event.Id, StringComparison.Ordinal)
            && streamEvent.Event.Data is AIToolOutputAvailableEventData { ToolName: "search", ProviderExecuted: true }).ToList();
        Assert.NotEmpty(toolOutputEvents);

        var firstToolOutputEvent = toolOutputEvents[0];

        Assert.True(events.IndexOf(toolInputEvent) < events.IndexOf(firstToolOutputEvent));
    }

    private static ZaiProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage StreamingResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

    private static string DecodeDataUrl(string dataUrl)
    {
        var commaIndex = dataUrl.IndexOf(',');
        Assert.True(commaIndex >= 0, $"Expected a valid data URL but found '{dataUrl}'.");

        var metadata = dataUrl[..commaIndex];
        var payload = dataUrl[(commaIndex + 1)..];

        return metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(payload))
            : Uri.UnescapeDataString(payload);
    }

    private static string CreateTempCaptureRoot()
        => Path.Combine(Path.GetTempPath(), "aihappey-zai-agent-capture-tests", Guid.NewGuid().ToString("N"));

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary capture output.
        }
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
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
}
