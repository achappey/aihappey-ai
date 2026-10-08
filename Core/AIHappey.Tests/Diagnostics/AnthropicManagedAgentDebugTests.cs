using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIHappey.Tests.Diagnostics;

public sealed class AnthropicManagedAgentDebugTests
{
    private const string Session = "{ \"id\":\"s1\",\"status\":\"idle\",\"agent\":{\"id\":\"a1\",\"version\":1,\"tools\":[]},\"usage\":{\"input_tokens\":2,\"output_tokens\":3} }";
    private const string Submitted = "{ \"data\":[{\"id\":\"u1\",\"type\":\"user.message\"}] }";
    private const string Message = "{\"id\":\"m1\",\"type\":\"agent.message\",\"content\":[{\"type\":\"text\",\"text\":\"Héllo 🌍\"}]}";
    private const string Terminal = "{\"id\":\"t1\",\"type\":\"session.status_idle\",\"stop_reason\":{\"type\":\"end_turn\"}}";
    private const string PendingHistory = "{ \"data\":[{\"id\":\"u1\",\"type\":\"user.message\"}] }";
    private static readonly string CompletedHistory = "{ \"data\":[{\"id\":\"u1\",\"type\":\"user.message\"}," + Message + "," + Terminal + "] }";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Json_bodies_are_exact_correlated_and_opt_in_including_stream_fallback(bool streaming, bool enabled)
    {
        var sink = new Sink();
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var provider = Provider(client, Events(enabled, sink));
        if (streaming)
        {
            var parts = await Collect(provider.StreamUnifiedAsync(Request()));
            Assert.Equal("Héllo 🌍", Assert.IsType<AITextDeltaEventData>(Assert.Single(parts, p => p.Event.Type == "text-delta").Event.Data).Delta);
            Assert.Equal("finish", parts[^1].Event.Type);
            Assert.DoesNotContain(parts, p => p.Event.Type.Contains("debug", StringComparison.Ordinal));
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(Request());
            Assert.Equal("completed", response.Status);
            Assert.Equal("Héllo 🌍", response.Output!.Items!.SelectMany(i => i.Content!)
                .OfType<AITextContentPart>().Single().Text);
        }
        if (!enabled)
        {
            Assert.Empty(sink.Items);
            return;
        }
        AssertCapturedCalls(handler, sink);
        if (streaming)
        {
            var errors = sink.Items.Where(e => e.Operation.Contains("/events/stream", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, errors.Length);
            Assert.All(errors, e =>
            {
                Assert.Equal("response-body", e.Kind);
                Assert.Equal("stream unavailable\r\n", e.Payload.Content);
                Assert.Equal("text/plain", e.Payload.MediaType);
            });
        }
    }

    [Fact]
    public async Task Direct_construction_without_debug_still_works()
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        Assert.NotNull(typeof(AnthropicProvider).GetConstructor([typeof(IApiKeyResolver), typeof(IHttpClientFactory)]));
        var provider = new AnthropicProvider(new KeyResolver(), new Factory(client));
        Assert.Equal("completed", (await provider.ExecuteUnifiedAsync(Request())).Status);
    }

    [Fact]
    public async Task Agent_and_environment_discovery_never_emit_debug_but_inference_still_does()
    {
        var sink = new Sink();
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/agents" => Json("{\"data\":[{\"id\":\"a1\",\"name\":\"agent\"}]}"),
            "/v1/environments" => Json("{\"data\":[{\"id\":\"e1\",\"name\":\"environment\"}]}"),
            _ => null
        });
        using var client = new HttpClient(handler);
        var provider = Provider(client, Events(true, sink));
        // Isolate managed-agent discovery: public ListModels also uses a separate
        // SDK transport for standard models, which must not make a live API call here.
        var discovery = typeof(AnthropicProvider).GetMethod("ListManagedAgentModelsSafeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var models = await (Task<IEnumerable<Model>>)discovery.Invoke(provider, [CancellationToken.None])!;
        Assert.Equal("anthropic/agent/a1/e1", Assert.Single(models).Id);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Empty(sink.Items);
        Assert.Equal("completed", (await provider.ExecuteUnifiedAsync(Request())).Status);
        Assert.NotEmpty(sink.Items);
        Assert.DoesNotContain(sink.Items, e => e.Operation.StartsWith("v1/agents?", StringComparison.Ordinal)
            || e.Operation.StartsWith("v1/environments?", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderAuth_chat_resolves_before_subscribing_and_sets_headers_before_debug_delivery(bool enabled)
    {
        using var handler = new Handler();
        using var upstream = new HttpClient(handler);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddProviderDebugEvents();
        builder.Services.AddControllers().AddApplicationPart(typeof(AIHappey.HeaderAuth.Controllers.ChatController).Assembly);
        builder.Services.AddScoped<IAIModelProviderResolver>(services =>
        {
            var debug = services.GetRequiredService<IProviderDebugEmitter>();
            return new Resolver(Provider(upstream, debug), async () =>
            {
                // An unexpected diagnostic during resolution must not start the
                // response, even for providers other than Anthropic.
                await debug.EmitAsync("anthropic", "discovery-probe", "probe", "response-body",
                    ProviderDebugPayload.FromText("{}", "application/json"));
                Assert.False(services.GetRequiredService<IHttpContextAccessor>().HttpContext!.Response.HasStarted);
            });
        });
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add(RequestDebugEvents.HeaderName, enabled ? "true" : "false");
            using var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest
            {
                Id = "headerauth-test", Model = "anthropic/agent/a1/e1",
                Messages = [new() { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "hello" }] }]
            });
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("v1", Assert.Single(response.Headers.GetValues("x-vercel-ai-ui-message-stream")));
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"type\":\"text-delta\"", body);
            Assert.Contains("\"type\":\"finish\"", body);
            Assert.DoesNotContain("\"type\":\"error\"", body);
            Assert.DoesNotContain("discovery-probe", body);
            if (enabled) Assert.Contains("data-aihappey-debug", body);
            else Assert.DoesNotContain("data-aihappey-debug", body);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Each_poll_is_delivered_immediately_with_a_distinct_operation_id()
    {
        var sink = new Sink();
        var polls = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/sessions/s1/events" || request.Method != HttpMethod.Get)
                return null;
            Assert.Equal(polls, sink.Items.Count(e => e.Kind == "response-body" && e.Operation.Contains("?order=asc", StringComparison.Ordinal)));
            return Json(++polls < 3 ? PendingHistory : CompletedHistory);
        });
        using var client = new HttpClient(handler);
        await Provider(client, Events(true, sink)).ExecuteUnifiedAsync(Request());
        Assert.Equal(3, polls);
        AssertCapturedCalls(handler, sink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_definition_lookup_and_session_synchronization_use_the_same_capture_boundary(bool existingSession)
    {
        var sink = new Sink();
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var request = new AIRequest
        {
            ProviderId = "anthropic", Model = "anthropic/agent/a1/e1",
            Tools = [new() { Name = "lookup", Description = "full tool description" }],
            Input = existingSession ? new() { Items =
            [
                new() { Role = "assistant", Content = [new AIToolCallContentPart
                {
                    Type = "tool-call", ToolName = "create_managed_agent_session", ToolCallId = "session",
                    ProviderExecuted = true, State = "output-available",
                    Output = new { sessionId = "s1", agentId = "a1", environmentId = "e1" }
                }] },
                new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "follow-up" }] }
            ] } : Request().Input
        };
        await Provider(client, Events(true, sink)).ExecuteUnifiedAsync(request);
        AssertCapturedCalls(handler, sink);
        var toolRequest = Assert.Single(sink.Items, e => e.Kind == "request-body" &&
            e.Operation == (existingSession ? "v1/sessions/s1" : "v1/sessions"));
        Assert.Contains("lookup", toolRequest.Payload.Content);
        Assert.Contains("full tool description", toolRequest.Payload.Content);
        if (!existingSession)
            Assert.Single(sink.Items, e => e.Operation == "v1/agents/a1" && e.Kind == "response-body");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Http_errors_and_malformed_json_are_captured_before_normal_error_handling(bool streaming, bool malformed)
    {
        var native = malformed ? "{broken json\r\n" : "native error\r\n with whitespace ";
        var sink = new Sink();
        using var handler = new Handler(_ => Json(native, malformed ? HttpStatusCode.OK : HttpStatusCode.BadRequest, "text/plain"));
        using var client = new HttpClient(handler);
        var provider = Provider(client, Events(true, sink));
        async Task Execute()
        {
            if (streaming) await Collect(provider.StreamUnifiedAsync(Request()));
            else await provider.ExecuteUnifiedAsync(Request());
        }
        if (malformed) await Assert.ThrowsAnyAsync<JsonException>(Execute);
        else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        var captured = Assert.Single(sink.Items, e => e.Kind == "response-body");
        Assert.Equal(native, captured.Payload.Content);
        Assert.Equal("text/plain", captured.Payload.MediaType);
        AssertCapturedCalls(handler, sink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_sse_is_preserved_before_filtering_on_initial_and_reconnected_streams(bool enabled)
    {
        const string first = ": keepalive\r\n\r\nevent: future\r\nid: native-id\r\nretry: 15\r\ndata: {\"type\":\"future\",\"text\":\"Héllo 🌍\"}\r\n\r\ndata: {broken}\n\ndata: [DONE]\n\n: EOF tail";
        var second = "data: " + Message + "\n\ndata: " + Terminal + "\n\n";
        var streams = new List<FragmentedStream>();
        var sink = new Sink();
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/events/stream", StringComparison.Ordinal))
                return Sse(streams.Count == 0 ? first : second, streams);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                return Json(PendingHistory);
            return null;
        });
        using var client = new HttpClient(handler);
        var parts = await Collect(Provider(client, Events(enabled, sink)).StreamUnifiedAsync(Request()));
        Assert.Equal("Héllo 🌍", Assert.IsType<AITextDeltaEventData>(Assert.Single(parts, p => p.Event.Type == "text-delta").Event.Data).Delta);
        Assert.Equal("finish", parts[^1].Event.Type);
        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.True(s.Disposed));
        if (!enabled)
        {
            Assert.Empty(sink.Items);
            return;
        }
        var connections = sink.Items.Where(e => e.Kind == "response-event").GroupBy(e => e.OperationId).ToArray();
        Assert.Equal(2, connections.Length);
        Assert.Equal(first, string.Concat(connections[0].Select(e => e.Payload.Content)));
        Assert.Equal(second, string.Concat(connections[1].Select(e => e.Payload.Content)));
        Assert.False(connections[0].Last().Payload.Complete);
        var future = Assert.Single(connections[0], e => e.Payload.EventName == "future");
        Assert.Equal("native-id", future.Payload.EventId);
        Assert.Equal("15", future.Payload.Retry);
        Assert.Equal("Héllo 🌍", future.Payload.Data?.GetProperty("text").GetString());
        Assert.All(connections.SelectMany(c => c), e => Assert.Equal("text/event-stream", e.Payload.MediaType));
    }

    [Fact]
    public async Task Debug_never_drains_input_after_a_terminal_event()
    {
        var streams = new List<FragmentedStream>();
        var sink = new Sink();
        var observed = "data: " + Terminal + "\n\n";
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/events/stream", StringComparison.Ordinal)
            ? Sse(observed + ": must not be read\n\n", streams) : null);
        using var client = new HttpClient(handler);
        await Collect(Provider(client, Events(true, sink)).StreamUnifiedAsync(Request()));
        Assert.Equal(observed, string.Concat(sink.Items.Where(e => e.Kind == "response-event").Select(e => e.Payload.Content)));
        Assert.Equal(Encoding.UTF8.GetByteCount(observed), Assert.Single(streams).BytesRead);
        Assert.True(streams[0].Disposed);
    }

    [Fact]
    public async Task Early_enumeration_disposal_stops_capture_and_disposes_the_connection()
    {
        var streams = new List<FragmentedStream>();
        var sink = new Sink();
        var observed = "data: " + Message + "\n\n";
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/events/stream", StringComparison.Ordinal)
            ? Sse(observed + "data: " + Terminal + "\n\n", streams) : null);
        using var client = new HttpClient(handler);
        await foreach (var part in Provider(client, Events(true, sink)).StreamUnifiedAsync(Request()))
            if (part.Event.Type == "text-delta") break;
        Assert.Equal(observed, string.Concat(sink.Items.Where(e => e.Kind == "response-event").Select(e => e.Payload.Content)));
        Assert.Equal(Encoding.UTF8.GetByteCount(observed), Assert.Single(streams).BytesRead);
        Assert.True(streams[0].Disposed);
        Assert.DoesNotContain(handler.Calls, c => c.Method == HttpMethod.Get && c.Operation == "v1/sessions/s1");
    }

    [Fact]
    public async Task Upstream_read_failure_still_recovers_when_debug_is_enabled()
    {
        var streams = new List<FragmentedStream>();
        var sink = new Sink();
        var first = "data: " + Message + "\n\n";
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/events/stream", StringComparison.Ordinal))
                return streams.Count == 0
                    ? Sse(first, streams, Encoding.UTF8.GetByteCount(first))
                    : Sse("data: " + Terminal + "\n\n", streams);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                return Json(PendingHistory);
            return null;
        });
        using var client = new HttpClient(handler);
        var parts = await Collect(Provider(client, Events(true, sink)).StreamUnifiedAsync(Request()));
        Assert.Equal("finish", parts[^1].Event.Type);
        Assert.Single(parts, p => p.Event.Type == "text-delta");
        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.True(s.Disposed));
        Assert.Equal(2, sink.Items.Where(e => e.Kind == "response-event").Select(e => e.OperationId).Distinct().Count());
    }

    [Fact]
    public async Task Reconnected_response_is_disposed_when_history_capture_fails()
    {
        var streams = new List<FragmentedStream>();
        var failure = new IOException("history debug sink failed");
        var sink = new Sink { OnWrite = (item, _) => item.Operation.Contains("?order=asc", StringComparison.Ordinal)
            ? throw failure : ValueTask.CompletedTask };
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/events/stream", StringComparison.Ordinal)
            ? Sse("", streams) : null);
        using var client = new HttpClient(handler);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Collect(Provider(client, Events(true, sink)).StreamUnifiedAsync(Request()))));
        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.True(s.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_stream_sink_failures_are_not_swallowed_by_recovery(bool reconnect)
    {
        var failure = new IOException("debug sink failed");
        var sink = new Sink { OnWrite = (item, _) => item.Kind == "response-event" ? throw failure : ValueTask.CompletedTask };
        var streams = new List<FragmentedStream>();
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/events/stream", StringComparison.Ordinal))
                return Sse(reconnect && streams.Count == 0 ? "" : ": native frame\n\n", streams);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                return Json(PendingHistory);
            return null;
        });
        using var client = new HttpClient(handler);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Collect(Provider(client, Events(true, sink)).StreamUnifiedAsync(Request()))));
        Assert.Equal(reconnect ? 2 : 1, streams.Count);
        Assert.All(streams, s => Assert.True(s.Disposed));
        Assert.DoesNotContain(handler.Calls, c => c.Method == HttpMethod.Get && c.Operation == "v1/sessions/s1");
    }

    [Theory]
    [InlineData("request-body")]
    [InlineData("response-body")]
    public async Task Json_sink_failures_propagate_without_further_upstream_calls(string kind)
    {
        var failure = new IOException("debug sink failed");
        var sink = new Sink { OnWrite = (item, _) => item.Kind == kind ? throw failure : ValueTask.CompletedTask };
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Provider(client, Events(true, sink)).ExecuteUnifiedAsync(Request())));
        Assert.Equal(kind == "request-body" ? 0 : 1, handler.Calls.Count);
    }

    [Fact]
    public async Task Failed_stream_opening_debug_delivery_does_not_fall_back()
    {
        var failure = new IOException("debug sink failed");
        var sink = new Sink { OnWrite = (item, _) => item.Operation.Contains("/events/stream", StringComparison.Ordinal)
            ? throw failure : ValueTask.CompletedTask };
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Collect(Provider(client, Events(true, sink)).StreamUnifiedAsync(Request()))));
        Assert.Equal(2, handler.Calls.Count);
        Assert.DoesNotContain(handler.Calls, c => c.Operation == "v1/sessions/s1/events");
    }

    [Theory]
    [InlineData("request-body")]
    [InlineData("response-event")]
    public async Task Cancellation_interrupts_debug_backpressure_without_recovery(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink { OnWrite = async (item, ct) =>
        {
            if (item.Kind != kind) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        } };
        var streams = new List<FragmentedStream>();
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/events/stream", StringComparison.Ordinal)
            ? Sse(": native frame\n\n", streams) : null);
        using var client = new HttpClient(handler);
        var execution = Collect(Provider(client, Events(true, sink)).StreamUnifiedAsync(Request(), cancellation.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(kind == "request-body" ? 0 : 3, handler.Calls.Count);
        Assert.All(streams, s => Assert.True(s.Disposed));
    }

    private static void AssertCapturedCalls(Handler handler, Sink sink)
    {
        var responses = sink.Items.Where(e => e.Kind == "response-body").ToArray();
        Assert.Equal(handler.Calls.Count, responses.Length);
        Assert.Equal(responses.Length, responses.Select(e => e.OperationId).Distinct().Count());
        for (var i = 0; i < handler.Calls.Count; i++)
        {
            var call = handler.Calls[i];
            var response = responses[i];
            Assert.Equal(call.Operation, response.Operation);
            Assert.Equal(call.Response, response.Payload.Content);
            var requests = sink.Items.Where(e => e.Kind == "request-body" && e.OperationId == response.OperationId).ToArray();
            if (call.Body is null) Assert.Empty(requests);
            else
            {
                var request = Assert.Single(requests);
                Assert.Equal(call.Body, request.Payload.Content);
                Assert.True(request.Sequence < response.Sequence);
                Assert.NotNull(request.Payload.Data);
            }
        }
        Assert.Equal(handler.Calls.Count(c => c.Body is not null), sink.Items.Count(e => e.Kind == "request-body"));
        Assert.Equal(Enumerable.Range(1, sink.Items.Count).Select(i => (long)i), sink.Items.Select(e => e.Sequence));
        Assert.All(sink.Items, e =>
        {
            Assert.Equal("anthropic", e.Provider);
            Assert.Equal("test-request", e.RequestId);
            Assert.NotEmpty(e.OperationId);
            Assert.Equal("text", e.Payload.Encoding);
            Assert.DoesNotContain("secret-not-in-debug", e.Payload.Content);
        });
    }

    private static AIRequest Request() => new()
    {
        ProviderId = "anthropic", Model = "anthropic/agent/a1/e1", Input = new() { Text = "upstream prompt 🌍" }
    };
    private static AnthropicProvider Provider(HttpClient client, IProviderDebugEmitter debug) => new(new KeyResolver(), new Factory(client), debug);
    private static RequestDebugEvents Events(bool enabled, Sink sink)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "test-request" };
        context.Request.Headers[RequestDebugEvents.HeaderName] = enabled ? "true" : "false";
        return new(new HttpContextAccessor { HttpContext = context }, [sink]);
    }
    private static async Task<List<AIStreamEvent>> Collect(IAsyncEnumerable<AIStreamEvent> stream)
    {
        var parts = new List<AIStreamEvent>();
        await foreach (var part in stream) parts.Add(part);
        return parts;
    }
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
        => new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };
    private static HttpResponseMessage Sse(string text, List<FragmentedStream> streams, int? failAfter = null)
    {
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes(text), failAfter);
        streams.Add(stream);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }
    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "secret-not-in-debug";
    }
    private sealed class Resolver(IModelProvider provider, Func<Task> onResolve) : IAIModelProviderResolver
    {
        public async Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
        {
            await onResolve();
            return provider;
        }
        public IModelProvider GetProvider() => provider;
        public Task<ModelResponse> ResolveModels(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Sink : IProviderDebugSink
    {
        public List<ProviderDebugEvent> Items { get; } = [];
        public Func<ProviderDebugEvent, CancellationToken, ValueTask>? OnWrite { get; init; }
        public ValueTask WriteAsync(ProviderDebugEvent item, CancellationToken ct)
        {
            Items.Add(item);
            return OnWrite?.Invoke(item, ct) ?? ValueTask.CompletedTask;
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage?>? responder = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Operation, string? Body, string? Response)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = responder?.Invoke(request) ?? (path switch
            {
                "/v1/sessions" or "/v1/sessions/s1" => Json(Session),
                "/v1/agents/a1" => Json("{\"id\":\"a1\",\"version\":1,\"tools\":[]}"),
                "/v1/sessions/s1/events" => Json(request.Method == HttpMethod.Post ? Submitted : CompletedHistory),
                "/v1/sessions/s1/events/stream" => Json("stream unavailable\r\n", HttpStatusCode.NotFound, "text/plain"),
                _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
            });
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var native = response.Content is StringContent ? await response.Content.ReadAsStringAsync(ct) : null;
            Calls.Add((request.Method, request.RequestUri.PathAndQuery.TrimStart('/'), body, native));
            response.RequestMessage = request;
            return response;
        }
    }
    private sealed class FragmentedStream(byte[] bytes, int? failAfter = null) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (failAfter.HasValue && BytesRead >= failAfter.Value)
                throw new IOException("upstream read failed");
            var count = await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
            BytesRead += count;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
