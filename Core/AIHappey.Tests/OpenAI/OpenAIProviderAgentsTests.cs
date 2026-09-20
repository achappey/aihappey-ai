using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIProviderAgentsTests
{
    [Fact]
    public async Task ListModels_adds_paginated_saved_agents()
    {
        var handler = new StaticResponseHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => JsonResponse(new { data = new[] { new { id = "gpt-test", created = 1L, owned_by = "openai" } } }),
            "/v1/agents" when !request.RequestUri.Query.Contains("after=") => JsonResponse(new
            {
                data = new[] { new { id = "agent_1", name = "Researcher", model = "gpt-6-astra", instructions = "Research carefully", created_at = 10L } },
                has_more = true,
                last_id = "agent_1"
            }),
            "/v1/agents" => JsonResponse(new
            {
                data = new[] { new { id = "agent_2", name = (string?)null, model = "gpt-6-astra", instructions = (string?)null, created_at = 11L } },
                has_more = false,
                last_id = "agent_2"
            }),
            _ => NotFound(request)
        });

        var models = (await CreateProvider(handler).ListModels()).ToList();

        var first = Assert.Single(models, model => model.Id == "openai/agent/agent_1");
        Assert.Equal("Researcher", first.Name);
        Assert.Contains("agent", first.Tags ?? []);
        Assert.Contains(models, model => model.Id == "openai/agent/agent_2");
    }

    [Fact]
    public async Task StreamUnifiedAsync_creates_hosted_session_maps_text_tools_reasoning_and_artifact()
    {
        string? createBody = null;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents")
                return JsonResponse(new { data = new[] { new { id = "agent_1", tools = Array.Empty<object>() } }, has_more = false });

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions")
            {
                Assert.Equal("agents=v1", Header(request, "OpenAI-Beta"));
                createBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return SseResponse(
                    new { type = "agent.session.created", event_id = "evt_created", session = new { id = "sess_1", environment = new { id = "env_1", type = "openai_hosted" }, status = "in_progress" } },
                    new { type = "agent.session.turn.reasoning_summary_text.delta", event_id = "evt_r1", session_id = "sess_1", turn_id = "turn_1", item_id = "reason_1", delta = "Checked facts." },
                    new { type = "agent.session.turn.reasoning_summary_text.done", event_id = "evt_r2", session_id = "sess_1", turn_id = "turn_1", item_id = "reason_1", text = "Checked facts." },
                    new { type = "agent.session.turn.item.done", event_id = "evt_tool", session_id = "sess_1", turn_id = "turn_1", item = new { id = "web_1", type = "web_search_call", turn_id = "turn_1", status = "completed", action = new { type = "search", query = "news" } } },
                    new { type = "agent.session.turn.output_text.delta", event_id = "evt_t1", session_id = "sess_1", turn_id = "turn_1", item_id = "msg_1", delta = "Hello" },
                    new { type = "agent.session.turn.output_text.done", event_id = "evt_t2", session_id = "sess_1", turn_id = "turn_1", item_id = "msg_1", text = "Hello" },
                    new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_1", turn_id = "turn_1", usage = new { input_tokens = 2, output_tokens = 3, total_tokens = 5 } });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_1/artifacts")
                return JsonResponse(new { data = new[] { new { id = "artifact_1", created_at = 10L, environment_id = "env_1", path = "/workspace/outputs/report.txt", session_id = "sess_1", size_bytes = 6L, turn_id = "turn_1" } }, has_more = false });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_1/artifacts/artifact_1/content")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("report"u8.ToArray()) };

            return NotFound(request);
        });

        var request = CreateRequest(
            tools:
            [
                new AIToolDefinition
                {
                    Name = "get_weather",
                    Description = "Gets weather",
                    InputSchema = new { type = "object", properties = new { city = new { type = "string" } } }
                }
            ],
            metadata: new Dictionary<string, object?>
            {
                ["openai"] = new
                {
                    environment = new
                    {
                        type = "openai_hosted",
                        network = new { access = "disabled" },
                        packages = new { python = new[] { "pandas==2.2.3" } }
                    }
                }
            });

        var events = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(request));

        Assert.NotNull(createBody);
        using var body = JsonDocument.Parse(createBody!);
        Assert.Equal("agent_1", body.RootElement.GetProperty("agent_id").GetString());
        Assert.Equal("openai_hosted", body.RootElement.GetProperty("environment").GetProperty("type").GetString());
        Assert.Equal("disabled", body.RootElement.GetProperty("environment").GetProperty("network").GetProperty("access").GetString());
        Assert.Contains(body.RootElement.GetProperty("agent").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "get_weather");

        var types = events.Select(value => value.Event.Type).ToList();
        FixtureAssertions.AssertContainsSubsequence(types,
            "tool-input-available", "tool-output-available",
            "reasoning-start", "reasoning-delta", "reasoning-end",
            "tool-input-available", "tool-output-available",
            "text-start", "text-delta", "text-end", "file", "finish");
        var file = Assert.IsType<AIFileEventData>(events.Single(value => value.Event.Type == "file").Event.Data);
        Assert.Equal("report.txt", file.Filename);
        Assert.Equal("data:text/plain;base64,cmVwb3J0", file.Url);
    }

    [Fact]
    public async Task StreamUnifiedAsync_emits_client_function_call_and_resumes_with_tool_result()
    {
        string? submittedBody = null;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
                return SseResponse(
                    new { type = "agent.session.requires_action", event_id = "evt_action", session = new { id = "sess_existing", status = "requires_action", required_actions = new[] { new { type = "function_call", turn_id = "turn_client", call_id = "call_1", name = "weather", arguments = new { city = "Amsterdam" } } } } });

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
            {
                submittedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new { ok = true });
            }

            return NotFound(request);
        });

        var request = CreateRequest(
            metadata: new Dictionary<string, object?> { ["openai"] = new { sessionId = "sess_existing" } },
            input: new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "assistant",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-call",
                                ToolCallId = "call_1",
                                ToolName = "weather",
                                ProviderExecuted = false,
                                State = "output-available",
                                Output = new { temperature = 18 },
                                Metadata = new Dictionary<string, object?> { ["openai.turn_id"] = "turn_client" }
                            }
                        ]
                    }
                ]
            });

        var events = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(request));

        Assert.NotNull(submittedBody);
        using var body = JsonDocument.Parse(submittedBody!);
        var result = body.RootElement.GetProperty("events")[0];
        Assert.Equal("agent.session.input.tool_result", result.GetProperty("type").GetString());
        Assert.Equal("turn_client", result.GetProperty("turn_id").GetString());
        Assert.Equal("call_1", result.GetProperty("call_id").GetString());
        var input = Assert.IsType<AIToolInputAvailableEventData>(events.Single(value => value.Event.Type == "tool-input-available").Event.Data);
        Assert.False(input.ProviderExecuted);
        Assert.Equal("tool-calls", Assert.IsType<AIFinishEventData>(events.Last().Event.Data).FinishReason);
    }

    [Fact]
    public async Task StreamUnifiedAsync_captures_raw_agent_sse_when_backend_capture_metadata_is_present()
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

            var handler = new StaticResponseHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/v1/agents" => JsonResponse(new { data = new[] { new { id = "agent_1", tools = Array.Empty<object>() } }, has_more = false }),
                "/v1/agents/sessions" => SseResponse(
                    new { type = "agent.session.created", event_id = "evt_created", session = new { id = "sess_capture", environment = new { id = "env_capture", type = "openai_hosted" }, status = "in_progress" } },
                    new { type = "agent.session.turn.output_text.delta", event_id = "evt_text", session_id = "sess_capture", turn_id = "turn_capture", item_id = "msg_capture", delta = "Capture me" },
                    new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_capture", turn_id = "turn_capture" }),
                "/v1/agents/sessions/sess_capture/artifacts" => JsonResponse(new { data = Array.Empty<object>(), has_more = false }),
                _ => NotFound(request)
            });

            var request = CreateRequest(metadata: new Dictionary<string, object?>
            {
                ["openai"] = JsonSerializer.SerializeToElement(new
                {
                    backend_capture = new
                    {
                        relativeDirectory = "openai-agent-stream-capture",
                        fileName = "agents-stream"
                    }
                }, JsonSerializerOptions.Web)
            });

            _ = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(request));

            var captureFile = Assert.Single(Directory.GetFiles(captureRoot, "*", SearchOption.AllDirectories));
            Assert.EndsWith(Path.Combine("openai-agent-stream-capture", "agents-stream.jsonl"), captureFile);

            var captured = await File.ReadAllTextAsync(captureFile);
            Assert.Contains("data:", captured);
            Assert.Contains("agent.session.turn.output_text.delta", captured);
            Assert.Contains("msg_capture", captured);
            Assert.Contains("Capture me", captured);
            Assert.Contains("agent.session.turn.completed", captured);
        }
        finally
        {
            ProviderBackendCapture.Configure(previousCaptureOptions);
            TryDeleteDirectory(captureRoot);
        }
    }

    private static AIRequest CreateRequest(
        AIInput? input = null,
        Dictionary<string, object?>? metadata = null,
        List<AIToolDefinition>? tools = null) => new()
    {
        ProviderId = "openai",
        Model = "openai/agent/agent_1",
        Input = input ?? new AIInput { Text = "Do the task" },
        Metadata = metadata,
        Tools = tools ?? []
    };

    private static OpenAIProvider CreateProvider(HttpMessageHandler handler)
        => new(
               new StaticApiKeyResolver(),
               new StaticHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") }),
               new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
               new StaticEndUserIdResolver());

    private static HttpResponseMessage SseResponse(params object[] events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events.Select(value => $"data: {JsonSerializer.Serialize(value, JsonSerializerOptions.Web)}\n\n")), Encoding.UTF8, "text/event-stream")
        };

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage NotFound(HttpRequestMessage request)
        => new(HttpStatusCode.NotFound) { Content = new StringContent($"Unhandled: {request.Method} {request.RequestUri}") };

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    private static string CreateTempCaptureRoot()
        => Path.Combine(Path.GetTempPath(), "aihappey-openai-agent-capture-tests", Guid.NewGuid().ToString("N"));

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
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => "test-user";
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
