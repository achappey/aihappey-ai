using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
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
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing")
                return JsonResponse(new { id = "sess_existing", status = "requires_action", required_actions = new[] { new { type = "function_call", call_id = "call_1", turn_id = "turn_client" } } });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
                return SseResponse(
                    new { type = "agent.session.requires_action", event_id = "evt_action", session = new { id = "sess_existing", status = "requires_action", required_actions = new[] { new { type = "function_call", turn_id = "turn_client", call_id = "call_1", name = "weather", arguments = new { city = "Amsterdam" } } } } });

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
            {
                submittedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                AssertAgentToolResultsAreText(submittedBody);
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
        using (var output = JsonDocument.Parse(result.GetProperty("output").GetString()!))
            Assert.Equal(18, output.RootElement.GetProperty("temperature").GetInt32());
        var input = Assert.IsType<AIToolInputAvailableEventData>(events.Single(value => value.Event.Type == "tool-input-available").Event.Data);
        Assert.False(input.ProviderExecuted);
        Assert.Equal("tool-calls", Assert.IsType<AIFinishEventData>(events.Last().Event.Data).FinishReason);
    }

    [Fact]
    public async Task StreamUnifiedAsync_resumes_client_function_result_with_turn_id_preserved_in_ui_call_metadata()
    {
        string? submittedBody = null;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents")
                return JsonResponse(new { data = new[] { new { id = "agent_1", tools = Array.Empty<object>() } }, has_more = false });

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions")
                return SseResponse(
                    new { type = "agent.session.created", event_id = "evt_created", session = new { id = "sess_client", environment = new { id = "env_client", type = "openai_hosted" }, status = "in_progress" } },
                    new { type = "agent.session.turn.item.done", event_id = "evt_call", session_id = "sess_client", turn_id = "turn_client", item = new { type = "function_call", id = "exec_1", call_id = "exec_1", turn_id = "turn_client", name = "weather", arguments = new { city = "Amsterdam" }, status = "in_progress" } },
                    new { type = "agent.session.requires_action", event_id = "evt_action", session_id = "sess_client", session = new { id = "sess_client", status = "requires_action" } });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_client")
                return JsonResponse(new { id = "sess_client", status = "requires_action", required_actions = new[] { new { type = "function_call", turn_id = "turn_client", call_id = "exec_1", name = "weather", arguments = new { city = "Amsterdam" } } } });

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_client/events")
                return SseResponse(new { type = "agent.session.turn.completed", event_id = "evt_resumed", session_id = "sess_client", turn_id = "turn_client" });

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_client/events")
            {
                submittedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                AssertAgentToolResultsAreText(submittedBody);
                return JsonResponse(new { ok = true });
            }

            return NotFound(request);
        });

        var provider = CreateProvider(handler);
        var firstEvents = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(CreateRequest()));
        var functionEvent = Assert.Single(firstEvents, value => value.Event.Type == "tool-input-available" && value.Event.Id == "exec_1");
        var uiCall = Assert.IsType<ToolCallPart>(Assert.Single(VercelUnifiedMapper.ToUIMessagePart(functionEvent.Event, "openai"), part => part is ToolCallPart));
        Assert.Equal("turn_client", uiCall.ProviderMetadata!["openai"]!["turn_id"].ToString());

        // The client combines the streamed input with its locally executed result in a tool invocation.
        var clientPart = JsonSerializer.Deserialize<ToolInvocationPart>(JsonSerializer.Serialize(new ToolInvocationPart
        {
            Type = "tool-weather",
            ToolCallId = uiCall.ToolCallId,
            Title = uiCall.ToolName,
            Input = uiCall.Input,
            State = "output-available",
            Output = new { _meta = new { }, content = Array.Empty<object>(), structuredContent = new { countries = new[] { new { name = "Poland" } } } },
            ProviderExecuted = false,
            CallProviderMetadata = uiCall.ProviderMetadata
        }, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        var unifiedInput = new UIMessage { Id = "message_1", Role = Role.assistant, Parts = [clientPart] }.ToUnifiedInputItem();
        var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(unifiedInput.Content!));
        Assert.False(tool.Metadata!.ContainsKey("openai.turn_id"));

        _ = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(CreateRequest(
            input: new AIInput { Items = [unifiedInput] },
            metadata: new Dictionary<string, object?> { ["openai"] = new { sessionId = "sess_client" } })));

        Assert.NotNull(submittedBody);
        using var body = JsonDocument.Parse(submittedBody!);
        var result = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("agent.session.input.tool_result", result.GetProperty("type").GetString());
        Assert.Equal("turn_client", result.GetProperty("turn_id").GetString());
        Assert.Equal("exec_1", result.GetProperty("call_id").GetString());
        using var output = JsonDocument.Parse(result.GetProperty("output").GetString()!);
        Assert.Equal("Poland", output.RootElement.GetProperty("structuredContent").GetProperty("countries")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task StreamUnifiedAsync_uses_each_client_function_calls_own_turn_id()
    {
        string? submittedBody = null;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing")
                return JsonResponse(new { id = "sess_existing", status = "requires_action", required_actions = new[] { new { type = "function_call", call_id = "call_1", turn_id = "turn_first" }, new { type = "function_call", call_id = "call_2", turn_id = "turn_second" } } });
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
                return SseResponse(new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_existing", turn_id = "turn_latest" });
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
            {
                submittedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                AssertAgentToolResultsAreText(submittedBody);
                return JsonResponse(new { ok = true });
            }
            return NotFound(request);
        });

        var invocations = new[] { ("call_1", "turn_first"), ("call_2", "turn_second") }
            .Select(call => new ToolInvocationPart
            {
                Type = "tool-weather", ToolCallId = call.Item1, Title = "weather", State = "output-available",
                Input = new { city = "Amsterdam" }, Output = new { temperature = 18 }, ProviderExecuted = false,
                CallProviderMetadata = new Dictionary<string, Dictionary<string, object>?>
                {
                    ["openai"] = new() { ["raw"] = new { type = "function_call", call_id = call.Item1, turn_id = call.Item2 } }
                }
            }).Cast<UIMessagePart>().ToList();
        var input = new UIMessage { Id = "message_1", Role = Role.assistant, Parts = invocations }.ToUnifiedInputItem();

        _ = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest(
            input: new AIInput { Items = [input] },
            metadata: new Dictionary<string, object?> { ["openai"] = new { sessionId = "sess_existing" } })));

        using var body = JsonDocument.Parse(submittedBody!);
        var results = body.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal("turn_first", results.Single(result => result.GetProperty("call_id").GetString() == "call_1").GetProperty("turn_id").GetString());
        Assert.Equal("turn_second", results.Single(result => result.GetProperty("call_id").GetString() == "call_2").GetProperty("turn_id").GetString());
    }

    [Theory]
    [InlineData("output-available", true)]
    [InlineData("output-error", false)]
    public async Task StreamUnifiedAsync_preserves_text_function_results_and_failure_status(string state, bool success)
    {
        string? submittedBody = null;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing")
                return JsonResponse(new { id = "sess_existing", status = "requires_action", required_actions = new[] { new { type = "function_call", call_id = "call_1", turn_id = "turn_client" } } });
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
                return SseResponse(new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_existing", turn_id = "turn_client" });
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
            {
                submittedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                AssertAgentToolResultsAreText(submittedBody);
                return JsonResponse(new { ok = true });
            }
            return NotFound(request);
        });

        var input = new AIInput { Items = [new AIInputItem
        {
            Role = "assistant",
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "call_1", ToolName = "weather", ProviderExecuted = false,
                State = state, Output = JsonSerializer.SerializeToElement("plain result"),
                Metadata = new Dictionary<string, object?> { ["openai.turn_id"] = "turn_client" }
            }]
        }] };

        _ = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest(
            input: input,
            metadata: new Dictionary<string, object?> { ["openai"] = new { sessionId = "sess_existing" } })));

        using var body = JsonDocument.Parse(submittedBody!);
        var result = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("plain result", result.GetProperty("output").GetString());
        Assert.Equal(success, result.GetProperty("success").GetBoolean());
        if (!success)
            Assert.Equal("plain result", result.GetProperty("error").GetString());
    }

    private static void AssertAgentToolResultsAreText(string body)
    {
        using var payload = JsonDocument.Parse(body);
        foreach (var result in payload.RootElement.GetProperty("events").EnumerateArray()
                     .Where(item => item.GetProperty("type").GetString() == "agent.session.input.tool_result"))
            Assert.Equal(JsonValueKind.String, result.GetProperty("output").ValueKind);
    }

    [Fact]
    public async Task StreamUnifiedAsync_does_not_emit_completed_function_call_snapshot_as_tool_result()
    {
        var handler = new StaticResponseHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/agents" => JsonResponse(new { data = new[] { new { id = "agent_1", tools = Array.Empty<object>() } }, has_more = false }),
            "/v1/agents/sessions" => SseResponse(
                new { type = "agent.session.created", event_id = "evt_created", session = new { id = "sess_snapshot", environment = new { type = "none" } } },
                new { type = "agent.session.turn.item.done", event_id = "evt_call", session_id = "sess_snapshot", turn_id = "turn_1", item = new { type = "function_call", id = "call_1", call_id = "call_1", turn_id = "turn_1", name = "weather", arguments = new { city = "Amsterdam" }, status = "completed" } },
                new { type = "agent.session.requires_action", event_id = "evt_action", session_id = "sess_snapshot", session = new { id = "sess_snapshot", status = "requires_action" } }),
            "/v1/agents/sessions/sess_snapshot" => JsonResponse(new { id = "sess_snapshot", status = "requires_action", required_actions = new[] { new { type = "function_call", turn_id = "turn_1", call_id = "call_1", name = "weather", arguments = new { city = "Amsterdam" } } } }),
            _ => NotFound(request)
        });

        var events = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest()));

        Assert.Single(events, value => value.Event.Type == "tool-input-available" && value.Event.Id == "call_1");
        Assert.DoesNotContain(events, value => value.Event.Type == "tool-output-available" && value.Event.Id == "call_1");
    }

    [Fact]
    public async Task StreamUnifiedAsync_submits_only_current_pending_call_after_previous_rounds()
    {
        string? submittedBody = null;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing")
                return JsonResponse(new { id = "sess_existing", status = "requires_action", required_actions = new[] { new { type = "function_call", call_id = "call_current", turn_id = "turn_current" } } });
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
                return SseResponse(new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_existing", turn_id = "turn_current" });
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/sessions/sess_existing/events")
            {
                submittedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                AssertAgentToolResultsAreText(submittedBody);
                return JsonResponse(new { ok = true });
            }
            return NotFound(request);
        });

        var input = new AIInput { Items = [new AIInputItem
        {
            Role = "assistant",
            Content =
            [
                AgentFunctionResult("call_old", "turn_old", new { value = "previous" }),
                AgentFunctionResult("call_current", "turn_current", new { value = "current" }),
                AgentFunctionResult("call_old", "turn_old", new { value = "different historical value" }),
                AgentFunctionResult("call_current", "turn_old", new { value = "stale turn" })
            ]
        }] };

        _ = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest(
            input: input, metadata: new Dictionary<string, object?> { ["openai"] = new { sessionId = "sess_existing" } })));

        using var body = JsonDocument.Parse(submittedBody!);
        var result = Assert.Single(body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("call_current", result.GetProperty("call_id").GetString());
        Assert.Equal("turn_current", result.GetProperty("turn_id").GetString());
        using var output = JsonDocument.Parse(result.GetProperty("output").GetString()!);
        Assert.Equal("current", output.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task StreamUnifiedAsync_rejects_conflicting_results_for_same_pending_call()
    {
        var handler = new StaticResponseHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/agents/sessions/sess_existing" => JsonResponse(new { id = "sess_existing", status = "requires_action", required_actions = new[] { new { type = "function_call", call_id = "call_1", turn_id = "turn_1" } } }),
            "/v1/agents/sessions/sess_existing/events" when request.Method == HttpMethod.Get => SseResponse(),
            _ => NotFound(request)
        });

        var input = new AIInput { Items = [new AIInputItem
        {
            Role = "assistant",
            Content = [AgentFunctionResult("call_1", "turn_1", "first"), AgentFunctionResult("call_1", "turn_1", "second")]
        }] };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest(
                input: input, metadata: new Dictionary<string, object?> { ["openai"] = new { sessionId = "sess_existing" } }))));
        Assert.Contains("conflicting outputs", exception.Message);
    }

    private static AIToolCallContentPart AgentFunctionResult(string callId, string turnId, object output) => new()
    {
        Type = "tool-call", ToolCallId = callId, ToolName = "weather", ProviderExecuted = false,
        State = "output-available", Output = output,
        Metadata = new Dictionary<string, object?> { ["openai.turn_id"] = turnId }
    };

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

    [Fact]
    public async Task StreamUnifiedAsync_does_not_replay_streamed_text_or_reasoning_item_snapshots()
    {
        var handler = new StaticResponseHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/agents" => JsonResponse(new { data = new[] { new { id = "agent_1", tools = Array.Empty<object>() } }, has_more = false }),
            "/v1/agents/sessions" => SseResponse(
                new { type = "agent.session.created", event_id = "evt_created", session = new { id = "sess_dedup", environment = new { id = "env_dedup", type = "openai_hosted" }, status = "in_progress" } },
                new { type = "agent.session.turn.reasoning_summary_text.delta", event_id = "evt_reason_delta", session_id = "sess_dedup", turn_id = "turn_dedup", item_id = "reason_dedup", delta = "Checked facts." },
                new { type = "agent.session.turn.reasoning_summary_text.done", event_id = "evt_reason_done", session_id = "sess_dedup", turn_id = "turn_dedup", item_id = "reason_dedup", text = "Checked facts." },
                new { type = "agent.session.turn.item.done", event_id = "evt_reason_item", session_id = "sess_dedup", turn_id = "turn_dedup", item = new { type = "reasoning", id = "reason_dedup", turn_id = "turn_dedup", summary = new[] { new { type = "summary_text", text = "Checked facts." } }, status = "completed" } },
                new { type = "agent.session.turn.output_text.delta", event_id = "evt_text_delta", session_id = "sess_dedup", turn_id = "turn_dedup", item_id = "msg_dedup", delta = "Hello" },
                new { type = "agent.session.turn.output_text.done", event_id = "evt_text_done", session_id = "sess_dedup", turn_id = "turn_dedup", item_id = "msg_dedup", text = "Hello" },
                new { type = "agent.session.turn.item.done", event_id = "evt_text_item", session_id = "sess_dedup", turn_id = "turn_dedup", item = new { type = "message", id = "msg_dedup", turn_id = "turn_dedup", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "Hello" } } } },
                new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_dedup", turn_id = "turn_dedup" }),
            "/v1/agents/sessions/sess_dedup/artifacts" => JsonResponse(new { data = Array.Empty<object>(), has_more = false }),
            _ => NotFound(request)
        });

        var events = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest()));

        Assert.Single(events, value => value.Event.Type == "text-start" && value.Event.Id == "msg_dedup");
        var textDelta = Assert.Single(events, value => value.Event.Type == "text-delta" && value.Event.Id == "msg_dedup");
        Assert.Equal("Hello", Assert.IsType<AITextDeltaEventData>(textDelta.Event.Data).Delta);
        Assert.Single(events, value => value.Event.Type == "text-end" && value.Event.Id == "msg_dedup");

        Assert.Single(events, value => value.Event.Type == "reasoning-start" && value.Event.Id == "reason_dedup");
        var reasoningDelta = Assert.Single(events, value => value.Event.Type == "reasoning-delta" && value.Event.Id == "reason_dedup");
        Assert.Equal("Checked facts.", Assert.IsType<AIReasoningDeltaEventData>(reasoningDelta.Event.Data).Delta);
        Assert.Single(events, value => value.Event.Type == "reasoning-end" && value.Event.Id == "reason_dedup");
    }

    [Fact]
    public async Task StreamUnifiedAsync_preserves_item_snapshot_fallback_without_deltas()
    {
        var handler = new StaticResponseHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/agents" => JsonResponse(new { data = new[] { new { id = "agent_1", tools = Array.Empty<object>() } }, has_more = false }),
            "/v1/agents/sessions" => SseResponse(
                new { type = "agent.session.created", event_id = "evt_created", session = new { id = "sess_snapshot", environment = new { id = "env_snapshot", type = "openai_hosted" }, status = "in_progress" } },
                new { type = "agent.session.turn.item.done", event_id = "evt_reason_item", session_id = "sess_snapshot", turn_id = "turn_snapshot", item = new { type = "reasoning", id = "reason_snapshot", turn_id = "turn_snapshot", summary = new[] { new { type = "summary_text", text = "Recovered reasoning." } }, status = "completed" } },
                new { type = "agent.session.turn.item.done", event_id = "evt_text_item", session_id = "sess_snapshot", turn_id = "turn_snapshot", item = new { type = "message", id = "msg_snapshot", turn_id = "turn_snapshot", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "Recovered text." } } } },
                new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_snapshot", turn_id = "turn_snapshot" }),
            "/v1/agents/sessions/sess_snapshot/artifacts" => JsonResponse(new { data = Array.Empty<object>(), has_more = false }),
            _ => NotFound(request)
        });

        var events = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest()));

        var textDelta = Assert.Single(events, value => value.Event.Type == "text-delta" && value.Event.Id == "msg_snapshot");
        Assert.Equal("Recovered text.", Assert.IsType<AITextDeltaEventData>(textDelta.Event.Data).Delta);
        var reasoningDelta = Assert.Single(events, value => value.Event.Type == "reasoning-delta" && value.Event.Id == "reason_snapshot");
        Assert.Equal("Recovered reasoning.", Assert.IsType<AIReasoningDeltaEventData>(reasoningDelta.Event.Data).Delta);
    }

    [Fact]
    public async Task StreamUnifiedAsync_emits_matching_input_before_environment_lifecycle_output()
    {
        const string environmentId = "ccarenv_capture";
        var handler = new StaticResponseHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/agents" => JsonResponse(new { data = new[] { new { id = "agent_1", tools = Array.Empty<object>() } }, has_more = false }),
            "/v1/agents/sessions" => SseResponse(
                new { type = "agent.session.created", event_id = "evt_created", session = new { id = "sess_lifecycle", environment = new { id = environmentId, type = "openai_hosted" }, status = "in_progress" } },
                new { type = "agent.session.environment.ready", event_id = "evt_environment_ready", session_id = "sess_lifecycle", turn_id = (string?)null, environment = new { id = environmentId, type = "openai_hosted", status = "ready", error = (object?)null } },
                new { type = "agent.session.turn.completed", event_id = "evt_done", session_id = "sess_lifecycle", turn_id = "turn_lifecycle" }),
            "/v1/agents/sessions/sess_lifecycle/artifacts" => JsonResponse(new { data = Array.Empty<object>(), has_more = false }),
            _ => NotFound(request)
        });

        var events = await FixtureAssertions.CollectAsync(CreateProvider(handler).StreamUnifiedAsync(CreateRequest()));
        var lifecycleId = $"openai-lifecycle-{environmentId}";
        var lifecycleEvents = events
            .Where(value => value.Event.Id == lifecycleId)
            .ToList();

        Assert.Collection(
            lifecycleEvents,
            inputEvent =>
            {
                Assert.Equal("tool-input-available", inputEvent.Event.Type);
                var input = Assert.IsType<AIToolInputAvailableEventData>(inputEvent.Event.Data);
                Assert.Equal("environment_ready", input.ToolName);
                Assert.True(input.ProviderExecuted);
            },
            outputEvent =>
            {
                Assert.Equal("tool-output-available", outputEvent.Event.Type);
                var output = Assert.IsType<AIToolOutputAvailableEventData>(outputEvent.Event.Data);
                Assert.Equal("environment_ready", output.ToolName);
                Assert.True(output.ProviderExecuted);
                Assert.True(output.Preliminary);
            });
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
