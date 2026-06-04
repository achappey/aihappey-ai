using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Hyperbrowser;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Hyperbrowser;

public class HyperbrowserProviderUnifiedTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_RoutesBrowserUseShortcutAndAppliesMetadataOverrides()
    {
        var requests = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var provider = CreateProvider(request =>
        {
            requests.Add(CloneRequest(request));
            if (request.Content is not null)
                bodies.Add(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/task/browser-use") == true)
                return JsonResponse(new { jobId = "job_browser", liveUrl = "https://live.example/browser" });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/api/task/browser-use/job_browser") == true)
                return JsonResponse(new
                {
                    jobId = "job_browser",
                    status = "completed",
                    liveUrl = "https://live.example/browser",
                    data = new { steps = new object[] { new { step = 1 } }, finalResult = "Browser result" }
                });

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "hyperbrowser",
            Model = "hyperbrowser/browser-use/gpt-4o-mini",
            Input = new AIInput { Text = "Find the product price" },
            Metadata = new Dictionary<string, object?>
            {
                ["hyperbrowser"] = new
                {
                    maxSteps = 7,
                    validateOutput = true,
                    useVision = false,
                    plannerLlm = "gemini-2.5-flash",
                    sessionOptions = new { useProxy = true, proxyCountry = "US" }
                }
            }
        });

        Assert.Equal("completed", response.Status);
        Assert.Contains(response.Output?.Items ?? [], item =>
            item.Content?.OfType<AITextContentPart>().Any(part => part.Text == "Browser result") == true);
        Assert.Contains(requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/api/task/browser-use"));

        using var doc = JsonDocument.Parse(bodies.Single());
        var root = doc.RootElement;
        Assert.Equal("Find the product price", root.GetProperty("task").GetString());
        Assert.Equal("gpt-4o-mini", root.GetProperty("llm").GetString());
        Assert.Equal(7, root.GetProperty("maxSteps").GetInt32());
        Assert.True(root.GetProperty("validateOutput").GetBoolean());
        Assert.False(root.GetProperty("useVision").GetBoolean());
        Assert.Equal("gemini-2.5-flash", root.GetProperty("plannerLlm").GetString());
        Assert.True(root.GetProperty("sessionOptions").GetProperty("useProxy").GetBoolean());
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_ExposesBrowserUseStepsToolCallsAndScreenshotFileParts()
    {
        const string screenshotBase64 = "iVBORw0KGgo=";
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/task/browser-use") == true)
                return JsonResponse(new { jobId = "job_browser_steps", liveUrl = "https://live.example/browser-steps" });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/api/task/browser-use/job_browser_steps") == true)
                return JsonResponse(new
                {
                    jobId = "job_browser_steps",
                    status = "completed",
                    liveUrl = "https://live.example/browser-steps",
                    data = new
                    {
                        finalResult = "BrowserUse done",
                        steps = new object[]
                        {
                            new
                            {
                                model_output = new
                                {
                                    current_state = new
                                    {
                                        evaluation_previous_goal = "No previous goal yet.",
                                        memory = "Need open page and extract content.",
                                        next_goal = "Open example page."
                                    },
                                    action = new object[]
                                    {
                                        new { go_to_url = new { url = "https://example.com" } },
                                        new { extract_content = new { goal = "Extract page heading" } }
                                    }
                                },
                                result = new object[]
                                {
                                    new { is_done = false, extracted_content = "Navigated to example", include_in_memory = true, success = true },
                                    new { is_done = false, extracted_content = "Example Domain", include_in_memory = true, success = true }
                                },
                                state = new { screenshot = screenshotBase64, url = "https://example.com", title = "Example Domain" },
                                metadata = new { step_number = 3, step_start_time = "2026-01-01T00:00:00Z", step_end_time = "2026-01-01T00:00:01Z", input_tokens = 321 }
                            }
                        }
                    }
                });

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "hyperbrowser",
            Model = "hyperbrowser/browser-use/gpt-4o-mini",
            Input = new AIInput { Text = "Open and extract" }
        });

        Assert.Equal("completed", response.Status);

        var reasoningPart = Assert.Single(response.Output!.Items!
            .SelectMany(item => item.Content ?? [])
            .OfType<AIReasoningContentPart>());
        Assert.Contains("Evaluation previous goal: No previous goal yet.", reasoningPart.Text);
        Assert.Contains("Memory: Need open page and extract content.", reasoningPart.Text);
        Assert.Contains("Next goal: Open example page.", reasoningPart.Text);
        Assert.Equal("Need open page and extract content.", reasoningPart.Metadata!["hyperbrowser.step.memory"]);
        Assert.Equal("Open example page.", reasoningPart.Metadata!["hyperbrowser.step.next_goal"]);
        Assert.Equal(3, AssertNumber(reasoningPart.Metadata!["hyperbrowser.step_idx"]));

        var toolParts = response.Output.Items!
            .SelectMany(item => item.Content ?? [])
            .OfType<AIToolCallContentPart>()
            .Where(part => part.ToolName is "go_to_url" or "extract_content")
            .ToList();
        Assert.Equal(2, toolParts.Count);
        Assert.All(toolParts, part => Assert.True(part.ProviderExecuted));

        var goToUrl = toolParts.Single(part => part.ToolName == "go_to_url");
        var goToUrlInput = Assert.IsType<JsonElement>(goToUrl.Input);
        Assert.Equal("https://example.com", goToUrlInput.GetProperty("url").GetString());
        Assert.Equal("output-available", goToUrl.State);

        var extract = toolParts.Single(part => part.ToolName == "extract_content");
        var extractOutput = Assert.IsType<CallToolResult>(extract.Output);
        Assert.Equal("Example Domain", Assert.IsType<TextContentBlock>(Assert.Single(extractOutput.Content)).Text);
        Assert.Equal("Example Domain", extractOutput.StructuredContent!.Value.GetProperty("extracted_content").GetString());

        var filePart = Assert.Single(response.Output.Items!
            .SelectMany(item => item.Content ?? [])
            .OfType<AIFileContentPart>());
        Assert.Equal("image/png", filePart.MediaType);
        Assert.Equal("browseruse-step-3.png", filePart.Filename);
        Assert.Equal($"data:image/png;base64,{screenshotBase64}", filePart.Data);
        Assert.Equal("browser_use_screenshot", filePart.Metadata!["hyperbrowser.file.kind"]);
    }

    [Fact]
    public async Task StreamUnifiedAsync_EmitsPreliminaryToolOutputsAndFinalText()
    {
        var pollCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/task/hyper-agent") == true)
                return JsonResponse(new { jobId = "job_agent", liveUrl = "https://live.example/agent" });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/api/task/hyper-agent/job_agent") == true)
            {
                pollCount++;
                return pollCount == 1
                    ? JsonResponse(new { jobId = "job_agent", status = "running", liveUrl = "https://live.example/agent", data = new { steps = new object[] { new { step = 1 } }, finalResult = (string?)null } })
                    : JsonResponse(new { jobId = "job_agent", status = "completed", liveUrl = "https://live.example/agent", data = new { steps = new object[] { new { step = 1 }, new { step = 2 } }, finalResult = "Agent final result" } });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var events = new List<AIStreamEvent>();
        await foreach (var evt in provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "hyperbrowser",
            Model = "hyperbrowser/hyper-agent/gpt-4o",
            Input = new AIInput { Text = "Research this" },
            Metadata = new Dictionary<string, object?>
            {
                ["hyperbrowser"] = new { pollIntervalMilliseconds = 100, pollTimeoutSeconds = 5 }
            }
        }))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e.Event.Type == "tool-input-available");
        var outputs = events.Where(e => e.Event.Type == "tool-output-available")
            .Select(e => Assert.IsType<AIToolOutputAvailableEventData>(e.Event.Data))
            .ToList();
        Assert.Contains(outputs, o => o.Preliminary == true);
        Assert.Contains(outputs, o => o.Preliminary == false);
        Assert.Contains(events, e => e.Event.Type == "source-url");
        Assert.Contains(events, e => e.Event.Type == "text-delta" && Assert.IsType<AITextDeltaEventData>(e.Event.Data).Delta.Contains("Agent final result"));
        Assert.Equal("finish", events.Last().Event.Type);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_ExposesHyperbrowserStepsLiveSourceAndUsage()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/task/hyper-agent") == true)
                return JsonResponse(new { jobId = "job_steps", liveUrl = "https://live.example/steps" });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/api/task/hyper-agent/job_steps") == true)
                return JsonResponse(new
                {
                    jobId = "job_steps",
                    status = "completed",
                    liveUrl = "https://live.example/steps",
                    metadata = new { inputTokens = 123, outputTokens = 45, numTaskStepsCompleted = 1 },
                    data = new
                    {
                        finalResult = "Task done",
                        steps = new object[]
                        {
                            new
                            {
                                idx = 0,
                                agentOutput = new
                                {
                                    thoughts = "I should open and extract the page.",
                                    memory = "Need page content.",
                                    nextGoal = "Open the page.",
                                    actions = new object[]
                                    {
                                        new { type = "goToUrl", actionDescription = "Open example", @params = new { url = "https://example.com" } },
                                        new { type = "extract", actionDescription = "Extract text", @params = new { objective = "Get body" } },
                                        new { type = "thinkAction", actionDescription = "Think about next steps", @params = new { thought = "I should validate the extracted page." } }
                                    }
                                },
                                actionOutputs = new object[]
                                {
                                    new { success = true, message = "Navigated" },
                                    new { success = false, message = "Extraction failed" },
                                    new { success = true, message = "A simple thought process about your next steps. You thought about: I should validate the extracted page." }
                                }
                            }
                        }
                    }
                });

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "hyperbrowser",
            Model = "hyperbrowser/hyper-agent/gpt-4o",
            Input = new AIInput { Text = "Research this" }
        });

        Assert.Equal("completed", response.Status);

        var usage = Assert.IsAssignableFrom<IDictionary<string, object?>>(response.Usage);
        Assert.Equal(123, AssertNumber(usage["inputTokens"]));
        Assert.Equal(45, AssertNumber(usage["outputTokens"]));
        Assert.Equal(168, AssertNumber(usage["totalTokens"]));
        Assert.Equal(1, AssertNumber(usage["numTaskStepsCompleted"]));

        var reasoningParts = response.Output!.Items!
            .SelectMany(item => item.Content ?? [])
            .OfType<AIReasoningContentPart>()
            .ToList();
        Assert.Equal(2, reasoningParts.Count);
        var reasoningPart = reasoningParts.Single(part => part.Text == "I should open and extract the page.");
        Assert.Equal("I should open and extract the page.", reasoningPart.Text);
        Assert.Equal("Need page content.", reasoningPart.Metadata!["hyperbrowser.step.memory"]);
        Assert.Equal("Open the page.", reasoningPart.Metadata!["hyperbrowser.step.next_goal"]);
        var thinkReasoningPart = reasoningParts.Single(part => part.Text == "I should validate the extracted page.");
        Assert.Equal("thinkAction", thinkReasoningPart.Metadata!["hyperbrowser.reasoning_source"]);

        var toolParts = response.Output.Items!
            .SelectMany(item => item.Content ?? [])
            .OfType<AIToolCallContentPart>()
            .Where(part => part.ToolName is "goToUrl" or "extract")
            .ToList();
        Assert.Equal(2, toolParts.Count);
        Assert.All(toolParts, part => Assert.True(part.ProviderExecuted));
        Assert.DoesNotContain(response.Output.Items!.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>(), part => part.ToolName == "thinkAction");

        var goToUrl = toolParts.Single(part => part.ToolName == "goToUrl");
        var goToUrlInput = Assert.IsType<JsonElement>(goToUrl.Input);
        Assert.Equal("https://example.com", goToUrlInput.GetProperty("url").GetString());
        Assert.Equal("output-available", goToUrl.State);
        var goToUrlOutput = Assert.IsType<CallToolResult>(goToUrl.Output);
        Assert.True(goToUrlOutput.StructuredContent!.Value.GetProperty("success").GetBoolean());

        var extract = toolParts.Single(part => part.ToolName == "extract");
        Assert.Equal("output-error", extract.State);
        var extractOutput = Assert.IsType<CallToolResult>(extract.Output);
        Assert.False(extractOutput.StructuredContent!.Value.GetProperty("success").GetBoolean());

        var source = Assert.Single(response.Output.Items!, item => item.Type == "source-url");
        Assert.Equal("https://live.example/steps", source.Metadata!["source.url"]);
        Assert.Equal("https://live.example/steps", source.Metadata!["chatcompletions.source.url"]);
    }

    [Fact]
    public async Task StreamUnifiedAsync_EmitsEachStepOnceSingleLiveSourceAndTokenUsage()
    {
        var pollCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/task/hyper-agent") == true)
                return JsonResponse(new { jobId = "job_stream_steps", liveUrl = "https://live.example/stream" });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/api/task/hyper-agent/job_stream_steps") == true)
            {
                pollCount++;
                return JsonResponse(new
                {
                    jobId = "job_stream_steps",
                    status = pollCount == 1 ? "running" : "completed",
                    liveUrl = "https://live.example/stream",
                    metadata = new { inputTokens = 10, outputTokens = 5, numTaskStepsCompleted = 1 },
                    data = new
                    {
                        finalResult = pollCount == 1 ? null : "Final",
                        steps = new object[]
                        {
                            new
                            {
                                idx = 0,
                                agentOutput = new
                                {
                                    thoughts = "Thinking once",
                                    memory = "Remember once",
                                    nextGoal = "Continue once",
                                    actions = new object[]
                                    {
                                        new { type = "clickElement", actionDescription = "Click", @params = new { index = 7 } },
                                        new { type = "thinkAction", actionDescription = "Think", @params = new { thought = "Reasoning action once" } }
                                    }
                                },
                                actionOutputs = new object[]
                                {
                                    new { success = true, message = "Clicked" },
                                    new { success = true, message = "A simple thought process about your next steps. You thought about: Reasoning action once" }
                                }
                            }
                        }
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var events = new List<AIStreamEvent>();
        await foreach (var evt in provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "hyperbrowser",
            Model = "hyperbrowser/hyper-agent/gpt-4o",
            Input = new AIInput { Text = "Do it" },
            Metadata = new Dictionary<string, object?>
            {
                ["hyperbrowser"] = new { pollIntervalMilliseconds = 100, pollTimeoutSeconds = 5 }
            }
        }))
        {
            events.Add(evt);
        }

        Assert.Single(events, e => e.Event.Type == "source-url");
        Assert.Single(events, e => e.Event.Type == "reasoning-delta" && Assert.IsType<AIReasoningDeltaEventData>(e.Event.Data).Delta == "Thinking once");
        Assert.Single(events, e => e.Event.Type == "reasoning-delta" && Assert.IsType<AIReasoningDeltaEventData>(e.Event.Data).Delta == "Reasoning action once");
        Assert.Single(events, e => e.Event.Type == "tool-input-available" && Assert.IsType<AIToolInputAvailableEventData>(e.Event.Data).ToolName == "clickElement");
        Assert.Single(events, e => e.Event.Type == "tool-output-available" && Assert.IsType<AIToolOutputAvailableEventData>(e.Event.Data).ToolName == "clickElement");
        Assert.DoesNotContain(events, e => e.Event.Type == "tool-input-available" && Assert.IsType<AIToolInputAvailableEventData>(e.Event.Data).ToolName == "thinkAction");

        var finish = Assert.IsType<AIFinishEventData>(events.Last().Event.Data);
        Assert.Equal(10, finish.InputTokens);
        Assert.Equal(5, finish.OutputTokens);
        Assert.Equal(15, finish.TotalTokens);
        Assert.Equal(10, finish.MessageMetadata!.InputTokens);
        Assert.Equal(5, finish.MessageMetadata.OutputTokens);
        Assert.Equal(15, finish.MessageMetadata.TotalTokens);
    }

    [Fact]
    public async Task StreamUnifiedAsync_EmitsBrowserUseStepsOnceAndScreenshotFileEvents()
    {
        const string screenshotBase64 = "iVBORw0KGgo=";
        var pollCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/task/browser-use") == true)
                return JsonResponse(new { jobId = "job_browser_stream", liveUrl = "https://live.example/browser-stream" });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/api/task/browser-use/job_browser_stream") == true)
            {
                pollCount++;
                return JsonResponse(new
                {
                    jobId = "job_browser_stream",
                    status = pollCount == 1 ? "running" : "completed",
                    liveUrl = "https://live.example/browser-stream",
                    data = new
                    {
                        finalResult = pollCount == 1 ? null : "Browser stream final",
                        steps = new object[]
                        {
                            new
                            {
                                model_output = new
                                {
                                    current_state = new
                                    {
                                        evaluation_previous_goal = "Started.",
                                        memory = "Remember BrowserUse step.",
                                        next_goal = "Click the result."
                                    },
                                    action = new object[]
                                    {
                                        new { click_element = new { index = 7 } }
                                    }
                                },
                                result = new object[]
                                {
                                    new { is_done = false, extracted_content = "Clicked element", include_in_memory = true, success = true }
                                },
                                state = new { screenshot = screenshotBase64, url = "https://example.com", title = "Example Domain" },
                                metadata = new { step_number = 1, input_tokens = 42 }
                            }
                        }
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var events = new List<AIStreamEvent>();
        await foreach (var evt in provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "hyperbrowser",
            Model = "hyperbrowser/browser-use/gpt-4o-mini",
            Input = new AIInput { Text = "Click this" },
            Metadata = new Dictionary<string, object?>
            {
                ["hyperbrowser"] = new { pollIntervalMilliseconds = 100, pollTimeoutSeconds = 5 }
            }
        }))
        {
            events.Add(evt);
        }

        Assert.Single(events, e => e.Event.Type == "reasoning-delta" && Assert.IsType<AIReasoningDeltaEventData>(e.Event.Data).Delta.Contains("Memory: Remember BrowserUse step."));
        Assert.Single(events, e => e.Event.Type == "tool-input-available" && Assert.IsType<AIToolInputAvailableEventData>(e.Event.Data).ToolName == "click_element");
        Assert.Single(events, e => e.Event.Type == "tool-output-available" && Assert.IsType<AIToolOutputAvailableEventData>(e.Event.Data).ToolName == "click_element");

        var fileEvent = Assert.Single(events, e => e.Event.Type == "file");
        var fileData = Assert.IsType<AIFileEventData>(fileEvent.Event.Data);
        Assert.Equal("image/png", fileData.MediaType);
        Assert.Equal("browseruse-step-1.png", fileData.Filename);
        Assert.Equal($"data:image/png;base64,{screenshotBase64}", fileData.Url);

        Assert.Contains(events, e => e.Event.Type == "text-delta" && Assert.IsType<AITextDeltaEventData>(e.Event.Data).Delta.Contains("Browser stream final"));
        Assert.Equal("finish", events.Last().Event.Type);
    }

    [Fact]
    public async Task ListModels_IncludesAllDocumentedShortcutModels()
    {
        var provider = CreateProvider(_ => JsonResponse(new { }));
        var models = (await provider.ListModels()).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("hyperbrowser/hyper-agent/gpt-5.2", models);
        Assert.Contains("hyperbrowser/hyper-agent/gemini-3-flash-preview", models);
        Assert.Contains("hyperbrowser/browser-use/gemini-2.0-flash", models);
        Assert.Contains("hyperbrowser/browser-use/claude-sonnet-4-20250514", models);
    }

    private static HyperbrowserProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(object value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        => new(request.Method, request.RequestUri);

    private static int AssertNumber(object? value)
        => value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var intValue) => intValue,
            _ => throw new Xunit.Sdk.XunitException($"Expected numeric value, got {value?.GetType().Name ?? "null"}.")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string providerId) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
