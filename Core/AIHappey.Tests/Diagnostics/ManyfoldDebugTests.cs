using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Http;
using AIHappey.Core.Providers.Manyfold;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Diagnostics;

public sealed class ManyfoldDebugTests
{
    private const string Success = "{ \"id\":\"c1\",\"model\":\"agent\",\"created\":1,\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"done\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3},\"metadata\":{\"session_id\":\"s1\",\"native\":true} }\r\n";
    private const string Keepalive = ": keepalive\r\n\r\n";
    private const string NativeStream = Keepalive
        + "event: future\r\nid: native-id\r\nretry: 100\r\ndata: {\"id\":\"c1\",\"choices\":[],\"future\":\"Héllo 🌍\"}\r\n\r\n"
        + "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"done\"},\"finish_reason\":null}],\"metadata\":{\"session_id\":\"s1\",\"native\":true}}\n\n"
        + "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}\n\n"
        + "data: [DONE]\n\n";

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("invalid")]
    [InlineData("true,false")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public async Task Bodies_are_exact_transformed_correlated_and_header_gated(string? header)
    {
        var sink = new Sink();
        using var fixture = new Fixture(_ => Response(Success), Events(header, sink));
        var response = await fixture.Provider.ExecuteUnifiedAsync(Request());
        Assert.Equal("completed", response.Status);
        var receipt = Assert.Single(response.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
        Assert.Equal("manyfold_session", receipt.ToolName);
        Assert.True(receipt.ProviderExecuted);
        Assert.Equal(Success.Trim(), ((JsonElement)response.Metadata!["chatcompletions.response.raw"]!).GetRawText());
        if (!string.Equals(header, "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Empty(sink.Items);
            return;
        }

        Assert.Equal(2, sink.Items.Count);
        AssertRequest(fixture, sink, false);
        var native = sink.Items[1];
        Assert.Equal("response-body", native.Kind);
        Assert.Equal(Success, native.Payload.Content);
        Assert.Equal("application/json", native.Payload.MediaType);
        Assert.True(native.Payload.Data?.GetProperty("metadata").GetProperty("native").GetBoolean());
        AssertCorrelation(sink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_stream_capture_preserves_output_and_does_not_drain_after_done(bool enabled)
    {
        var sink = new Sink();
        using var native = new FragmentedStream(NativeStream + ": unread tail\n\n");
        using var fixture = new Fixture(_ => StreamResponse(native), Events(enabled ? "true" : null, sink));
        var parts = await Collect(fixture.Provider);
        Assert.Equal(Encoding.UTF8.GetByteCount(NativeStream), native.BytesRead);
        Assert.Equal("done", Assert.Single(parts.Select(e => e.Event.Data).OfType<AITextDeltaEventData>()).Delta);
        Assert.Contains(parts, e => e.Event.Type == "tool-input-available");
        Assert.Contains(parts, e => e.Event.Type == "tool-output-available");
        Assert.Contains(parts, e => e.Event.Type == "data-manyfold-metadata");
        Assert.DoesNotContain(parts, e => e.Event.Type == ChatSseWriter.DebugPartType);
        var finish = Assert.Single(parts.Select(e => e.Event.Data).OfType<AIFinishEventData>());
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal(3, finish.TotalTokens);
        if (!enabled)
        {
            Assert.Empty(sink.Items);
            return;
        }

        AssertRequest(fixture, sink, true);
        var frames = sink.Items.Where(e => e.Kind == "response-event").ToArray();
        Assert.Equal(NativeStream, string.Concat(frames.Select(e => e.Payload.Content)));
        Assert.Equal(Keepalive, frames[0].Payload.Content);
        Assert.Equal("future", frames[1].Payload.EventName);
        Assert.Equal("native-id", frames[1].Payload.EventId);
        Assert.Equal("100", frames[1].Payload.Retry);
        Assert.Equal("Héllo 🌍", frames[1].Payload.Data?.GetProperty("future").GetString());
        Assert.Equal("[DONE]", frames[^1].Payload.Data?.GetString());
        Assert.All(frames, e => Assert.True(e.Payload.Complete));
        AssertCorrelation(sink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_errors_are_captured_before_the_existing_exception(bool streaming)
    {
        const string body = " native error\r\n with whitespace ";
        var sink = new Sink();
        using var fixture = new Fixture(_ => Response(body, HttpStatusCode.BadRequest, "text/plain"), Events("true", sink));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (streaming) await Collect(fixture.Provider);
            else await fixture.Provider.ExecuteUnifiedAsync(Request());
        });
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal($"Manyfold error (400): {body}", error.Message);
        var captured = Assert.Single(sink.Items, e => e.Kind == "response-body");
        Assert.Equal(body, captured.Payload.Content);
        Assert.Equal(body, captured.Payload.Data?.GetString());
        Assert.Equal("text/plain", captured.Payload.MediaType);
        AssertCorrelation(sink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_json_is_captured_before_parsing_without_draining_the_stream(bool streaming)
    {
        const string malformed = " {bad} ";
        var observed = streaming ? $"data: {malformed}\r\n\r\n" : malformed;
        var sink = new Sink();
        using var native = new FragmentedStream(observed + "data: unread\n\n");
        using var fixture = new Fixture(_ => streaming ? StreamResponse(native) : Response(observed), Events("true", sink));
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        {
            if (streaming) await Collect(fixture.Provider);
            else await fixture.Provider.ExecuteUnifiedAsync(Request());
        });
        Assert.Equal(observed, sink.Items[1].Payload.Content);
        if (streaming) Assert.Equal(Encoding.UTF8.GetByteCount(observed), native.BytesRead);
    }

    [Fact]
    public async Task Eof_tail_is_captured_as_incomplete_without_changing_finish_handling()
    {
        var sink = new Sink();
        var observed = NativeStream[..NativeStream.LastIndexOf("data: [DONE]", StringComparison.Ordinal)] + ": eof tail 🌍";
        using var native = new FragmentedStream(observed);
        using var fixture = new Fixture(_ => StreamResponse(native), Events("true", sink));
        Assert.Contains(await Collect(fixture.Provider), e => e.Event.Type == "finish");
        Assert.Equal(observed, string.Concat(sink.Items.Skip(1).Select(e => e.Payload.Content)));
        Assert.False(sink.Items[^1].Payload.Complete);
    }

    [Fact]
    public async Task Each_call_has_a_distinct_operation_id_and_direct_construction_remains_supported()
    {
        var sink = new Sink();
        using (var fixture = new Fixture(_ => Response(Success), Events("true", sink)))
        {
            await fixture.Provider.ExecuteUnifiedAsync(Request());
            await fixture.Provider.ExecuteUnifiedAsync(Request());
            Assert.Equal(2, sink.Items.Select(e => e.OperationId).Distinct().Count());
            Assert.Equal(sink.Items[0].OperationId, sink.Items[1].OperationId);
            Assert.Equal(sink.Items[2].OperationId, sink.Items[3].OperationId);
        }
        using var direct = new Fixture(_ => Response(Success));
        Assert.Equal("completed", (await direct.Provider.ExecuteUnifiedAsync(Request())).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_discovery_remains_uninstrumented_including_http_errors(bool fail)
    {
        var sink = new Sink();
        using var fixture = new Fixture(request =>
        {
            Assert.Equal("/api/agents", request.RequestUri!.AbsolutePath);
            return fail ? Response("discovery error", HttpStatusCode.BadRequest)
                : Response("[{\"id\":\"agent\",\"name\":\"Agent\"}]");
        }, Events("true", sink));
        if (fail)
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.ListModels());
        else
            Assert.Equal("manyfold/agent", Assert.Single(await fixture.Provider.ListModels()).Id);
        Assert.Empty(sink.Items);
    }

    [Fact]
    public async Task Early_disposal_does_not_drain_the_remaining_native_stream()
    {
        var sink = new Sink();
        using var native = new FragmentedStream(NativeStream);
        using var fixture = new Fixture(_ => StreamResponse(native), Events("true", sink));
        await using (var enumerator = fixture.Provider.StreamUnifiedAsync(Request()).GetAsyncEnumerator())
            Assert.True(await enumerator.MoveNextAsync());
        var observed = string.Concat(sink.Items.Where(e => e.Kind == "response-event").Select(e => e.Payload.Content));
        Assert.StartsWith(Keepalive, observed);
        Assert.True(observed.Length < NativeStream.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(observed), native.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_sink_failures_propagate_before_submission(bool streaming)
    {
        var sink = new Sink { OnWrite = (_, _) => throw new IOException("debug sink failed") };
        using var fixture = new Fixture(_ => Response(Success), Events("true", sink));
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (streaming) await Collect(fixture.Provider);
            else await fixture.Provider.ExecuteUnifiedAsync(Request());
        });
        Assert.Equal("debug sink failed", error.Message);
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Fact]
    public async Task Response_body_sink_failures_propagate_before_parsing()
    {
        var sink = new Sink { OnWrite = (e, _) => e.Kind == "response-body"
            ? throw new IOException("debug sink failed") : ValueTask.CompletedTask };
        using var fixture = new Fixture(_ => Response("{bad}"), Events("true", sink));
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Provider.ExecuteUnifiedAsync(Request()));
        Assert.Equal("debug sink failed", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_sink_backpressure_failure_and_cancellation_do_not_read_later_frames(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink { OnWrite = async (e, ct) =>
        {
            if (e.Kind != "response-event") return;
            entered.TrySetResult();
            if (cancel) await Task.Delay(Timeout.Infinite, ct);
            else throw new IOException("debug sink failed");
        } };
        using var native = new FragmentedStream(NativeStream);
        using var fixture = new Fixture(_ => StreamResponse(native), Events("true", sink));
        var execution = Collect(fixture.Provider, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            Assert.False(execution.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        else
            Assert.Equal("debug sink failed", (await Assert.ThrowsAsync<IOException>(() => execution)).Message);
        Assert.Equal(Encoding.UTF8.GetByteCount(Keepalive), native.BytesRead);
    }

    [Fact]
    public async Task Chat_writer_delivers_manyfold_debug_as_transient_parts_alongside_normal_output()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "manyfold-chat" };
        context.Request.Headers[RequestDebugEvents.HeaderName] = "true";
        using var body = new MemoryStream();
        context.Response.Body = body;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddProviderDebugEvents();
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        context.RequestServices = scope.ServiceProvider;
        var emitter = scope.ServiceProvider.GetRequiredService<IProviderDebugEmitter>();
        using var native = new FragmentedStream(NativeStream);
        using var fixture = new Fixture(_ => StreamResponse(native), emitter);
        using (var writer = new ChatSseWriter(context))
        {
            await foreach (var part in fixture.Provider.StreamAsync(new()
            {
                Model = "manyfold/agent",
                Messages = [new() { Id = "user", Role = AIHappey.Vercel.Models.Role.user,
                    Parts = [new AIHappey.Vercel.Models.TextUIPart { Text = "hello" }] }]
            }))
                await writer.WriteAsync(part);
        }
        var parts = Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => JsonSerializer.Deserialize<JsonElement>(frame["data: ".Length..])).ToArray();
        var debug = parts.Where(p => p.GetProperty("type").GetString() == ChatSseWriter.DebugPartType).ToArray();
        Assert.Equal(6, debug.Length);
        Assert.All(debug, p =>
        {
            Assert.True(p.GetProperty("transient").GetBoolean());
            Assert.Equal("manyfold", p.GetProperty("data").GetProperty("provider").GetString());
        });
        Assert.Contains(parts, p => p.GetProperty("type").GetString() == "text-delta" && p.GetProperty("delta").GetString() == "done");
    }

    private static AIRequest Request() => new()
    {
        Id = "request", ProviderId = "manyfold", Model = "manyfold/agent", Input = new() { Text = "upstream prompt 🌍" },
        Metadata = new() { ["manyfold"] = new { session_id = "s1", store = true, headers = new { debug = true }, debug = true } }
    };

    private static void AssertRequest(Fixture fixture, Sink sink, bool streaming)
    {
        var sent = Assert.Single(sink.Items, e => e.Kind == "request-body");
        Assert.Equal(Assert.Single(fixture.Handler.Bodies), sent.Payload.Content);
        var payload = sent.Payload.Data!.Value;
        Assert.Equal("agent", payload.GetProperty("model").GetString());
        Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
        Assert.Equal("s1", payload.GetProperty("metadata").GetProperty("session_id").GetString());
        Assert.False(payload.TryGetProperty("session_id", out _));
        Assert.False(payload.TryGetProperty("headers", out _));
        Assert.False(payload.TryGetProperty("store", out _));
        if (streaming) Assert.True(payload.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        else Assert.False(payload.TryGetProperty("stream_options", out _));
    }

    private static void AssertCorrelation(Sink sink)
    {
        Assert.NotEmpty(sink.Items[0].OperationId);
        Assert.Equal(Enumerable.Range(1, sink.Items.Count).Select(i => (long)i), sink.Items.Select(e => e.Sequence));
        Assert.All(sink.Items, e =>
        {
            Assert.Equal("manyfold", e.Provider);
            Assert.Equal("api/v1/chat/completions", e.Operation);
            Assert.Equal("test-request", e.RequestId);
            Assert.Equal(sink.Items[0].OperationId, e.OperationId);
            Assert.DoesNotContain("secret-not-in-debug", e.Payload.Content);
        });
    }

    private static RequestDebugEvents Events(string? header, Sink sink)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "test-request" };
        if (header is not null) context.Request.Headers[RequestDebugEvents.HeaderName] = header;
        return new(new HttpContextAccessor { HttpContext = context }, [sink]);
    }

    private static async Task<List<AIStreamEvent>> Collect(ManyfoldProvider provider, CancellationToken ct = default)
    {
        var result = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(Request(), ct)) result.Add(item);
        return result;
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly HttpClient client;
        public Handler Handler { get; }
        public ManyfoldProvider Provider { get; }
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> responder, IProviderDebugEmitter? debug = null)
        {
            Handler = new(responder);
            client = new(Handler);
            Provider = debug is null
                ? new(new KeyResolver(), new AsyncCacheHelper(cache), new Factory(client))
                : new(new KeyResolver(), new AsyncCacheHelper(cache), new Factory(client), debug);
        }
        public void Dispose() { client.Dispose(); cache.Dispose(); }
    }

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "secret-not-in-debug";
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string?> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return responder(request);
        }
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
    private sealed class FragmentedStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
            BytesRead += count;
            return count;
        }
    }
}
