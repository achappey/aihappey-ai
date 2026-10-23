using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Http;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Diagnostics;

public sealed class GoogleInteractionDebugTests
{
    private const string Completed = "{\"id\":\"i-1\",\"status\":\"completed\",\"model\":\"gemini-test\"}";
    private const string Pending = "{\"id\":\"i-1\",\"status\":\"in_progress\"}";
    private const string Native = ": keepalive\r\n\r\nevent: native\r\nid: n-1\r\nretry: 123\r\n\r\n"
        + "data: {\"event_type\":\"interaction.created\",\"interaction\":{\"id\":\"i-1\",\"model\":\"gemini-test\"}}\r\n\r\n"
        + "data: {\"event_type\":\"step.delta\",\"index\":0,\"delta\":{\"type\":\"text\",\"text\":\"Héllo 🌍\"}}\n\n"
        + "data: {\"event_type\":\"interaction.completed\",\"interaction\":{\"id\":\"i-1\",\"model\":\"gemini-test\",\"status\":\"completed\"}}\n\n";

    [Theory]
    [InlineData("gemini-test")]
    [InlineData("antigravity-preview")]
    [InlineData("agents/custom")]
    [InlineData("deep-research-preview")]
    public async Task Json_captures_exact_native_bodies_once_per_call_including_polls_and_cleanup(string model)
    {
        var agent = model != "gemini-test";
        var research = model.StartsWith("deep-research");
        var responses = new List<HttpResponseMessage> { Json(agent ? Pending : Completed) };
        if (research) responses.Add(Json(Pending)); // Every poll must survive, not only the final snapshot.
        if (agent) responses.Add(Json(Completed));
        if (research) responses.Add(Json("{\"deleted\":true}"));
        var handler = new Handler(responses);
        var sink = new Sink();
        var result = await Provider(handler, Events("true", sink)).GetInteraction(Request(model));

        Assert.Equal("completed", result.Status);
        Assert.Equal(responses.Count + 1, sink.Items.Count);
        Assert.Equal(handler.Bodies[0], sink.Items[0].Payload.Content);
        Assert.Equal("request-body", sink.Items[0].Kind);
        Assert.Equal(sink.Items[0].OperationId, sink.Items[1].OperationId);
        Assert.Equal(responses.Count, sink.Items.Select(e => e.OperationId).Distinct().Count());
        Assert.Equal(responses.Count, sink.Items.Count(e => e.Kind == "response-body"));
        Assert.All(sink.Items, e =>
        {
            Assert.Equal("google", e.Provider);
            Assert.Equal("application/json", e.Payload.MediaType);
            Assert.NotNull(e.Payload.Data);
        });
        Assert.Equal(agent ? Pending : Completed, sink.Items[1].Payload.Content);
        Assert.Equal("v1beta/interactions", sink.Items[0].Operation);
        Assert.All(sink.Items.Skip(2), e => Assert.Equal("v1beta/interactions/i-1", e.Operation));
        Assert.Equal(research ? HttpMethod.Delete : agent ? HttpMethod.Get : HttpMethod.Post, handler.Methods[^1]);
        Assert.All(handler.Bodies.Skip(1), body => Assert.Null(body));
        Assert.DoesNotContain("secret-api-key", JsonSerializer.Serialize(sink.Items));
    }

    [Theory]
    [InlineData("gemini-test")]
    [InlineData("antigravity-preview")]
    [InlineData("agents/custom")]
    [InlineData("deep-research-preview")]
    public async Task Native_SSE_is_observed_once_before_mapping_with_split_utf8_and_EOF_tail(string model)
    {
        const string tail = ": unfinished tail";
        var handler = new Handler([Sse(Native + tail), Json("{}")]);
        var sink = new Sink();
        var normal = await Read(Provider(handler, Events("true", sink)), Request(model));

        Assert.Equal(3, normal.Count);
        var frames = sink.Items.Where(e => e.Kind == "response-event").ToList();
        Assert.Equal(Native + tail, string.Concat(frames.Select(e => e.Payload.Content)));
        Assert.Equal(6, frames.Count);
        Assert.False(frames[^1].Payload.Complete);
        Assert.Equal("native", frames[1].Payload.EventName);
        Assert.Equal("n-1", frames[1].Payload.EventId);
        Assert.Equal("123", frames[1].Payload.Retry);
        Assert.All(frames, e => Assert.Equal(sink.Items[0].OperationId, e.OperationId));
        Assert.Equal(handler.Bodies[0], sink.Items[0].Payload.Content);
        Assert.Equal(model.StartsWith("deep-research") ? 8 : 7, sink.Items.Count);
    }

    [Theory]
    [InlineData("gemini-test", false)]
    [InlineData("gemini-test", true)]
    [InlineData("antigravity-preview", false)]
    [InlineData("antigravity-preview", true)]
    public async Task Http_errors_are_emitted_before_throwing(string model, bool stream)
    {
        const string error = "not JSON: upstream rejected the request";
        var handler = new Handler([Json(error, HttpStatusCode.BadRequest)]);
        var sink = new Sink();
        var provider = Provider(handler, Events("true", sink));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (stream) await Read(provider, Request(model));
            else await provider.GetInteraction(Request(model));
        });
        Assert.Contains(error, exception.Message);
        Assert.Equal(2, sink.Items.Count);
        Assert.Equal(error, sink.Items[1].Payload.Content);
        Assert.Equal(error, sink.Items[1].Payload.Data!.Value.GetString());
        Assert.Equal(sink.Items[0].OperationId, sink.Items[1].OperationId);
    }

    [Theory]
    [InlineData("gemini-test")]
    [InlineData("antigravity-preview")]
    public async Task Invalid_json_is_captured_before_deserialization(string model)
    {
        var handler = new Handler([Json("{bad json")]);
        var sink = new Sink();
        await Assert.ThrowsAsync<JsonException>(() => Provider(handler, Events("true", sink)).GetInteraction(Request(model)));
        Assert.Equal("{bad json", sink.Items[1].Payload.Content);
    }

    [Theory]
    [InlineData("gemini-test", null)]
    [InlineData("gemini-test", "false")]
    [InlineData("antigravity-preview", "invalid")]
    [InlineData("agents/custom", "false")]
    [InlineData("deep-research-preview", null)]
    public async Task Disabled_capture_keeps_normal_stream_and_outbound_body_unchanged(string model, string? header)
    {
        var enabledHandler = new Handler([Sse(Native), Json("{}")]);
        var disabledHandler = new Handler([Sse(Native), Json("{}")]);
        var enabled = new Sink();
        var disabled = new Sink();
        var expected = await Read(Provider(enabledHandler, Events("true", enabled)), Request(model));
        var actual = await Read(Provider(disabledHandler, Events(header, disabled)), Request(model));
        Assert.Empty(disabled.Items);
        Assert.NotEmpty(enabled.Items);
        Assert.Equal(enabledHandler.Bodies, disabledHandler.Bodies);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

        var noEmitterHandler = new Handler([Sse(Native), Json("{}")]);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await Read(Provider(noEmitterHandler), Request(model))));
    }

    [Theory]
    [InlineData("gemini-test", false)]
    [InlineData("gemini-test", true)]
    [InlineData("antigravity-preview", false)]
    [InlineData("antigravity-preview", true)]
    public async Task Early_disposal_and_DONE_do_not_drain_native_stream(string model, bool readCompleteMarkerFrame)
    {
        var native = new FragmentedStream(Native + "data: [DONE]\n\n" + new string('x', 2048));
        var handler = new Handler([new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(native) }]);
        var sink = new Sink();
        await using (var iterator = Provider(handler, Events("true", sink)).GetInteractions(Request(model)).GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        Assert.True(native.BytesRead < Encoding.UTF8.GetByteCount(Native));

        const string markerFrame = "data: [DONE]\n\n";
        var doneNative = new FragmentedStream(markerFrame + new string('x', 2048),
            readCompleteMarkerFrame ? Encoding.UTF8.GetByteCount(markerFrame) : 1);
        var doneHandler = new Handler([new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(doneNative) }]);
        var doneSink = new Sink();
        Assert.Empty(await Read(Provider(doneHandler, Events("true", doneSink)), Request(model)));
        Assert.True(doneNative.BytesRead < 100);
        Assert.Equal(Encoding.UTF8.GetByteCount(readCompleteMarkerFrame ? markerFrame : "data: [DONE]\n"), doneNative.BytesRead);
        var requestEvent = Assert.Single(doneSink.Items, item => item.Kind == "request-body");
        var responseEvents = doneSink.Items.Where(item => item.Kind == "response-event").ToArray();
        if (readCompleteMarkerFrame)
        {
            var responseEvent = Assert.Single(responseEvents);
            Assert.Equal(markerFrame, responseEvent.Payload.Content);
            Assert.True(responseEvent.Payload.Complete);
            Assert.Equal("[DONE]", responseEvent.Payload.Data!.Value.GetString());
            Assert.Equal(requestEvent.OperationId, responseEvent.OperationId);
        }
        else
        {
            // A line-based parser stops before reading the blank delimiter; do not invent or drain it.
            Assert.Empty(responseEvents);
            Assert.Single(doneSink.Items);
        }
    }

    [Theory]
    [InlineData("gemini-test")]
    [InlineData("antigravity-preview")]
    public async Task Awaited_sink_applies_backpressure_and_cancellation_before_upstream_send(string model)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        var handler = new Handler([]);
        using var cancellation = new CancellationTokenSource();
        var execution = Read(Provider(handler, Events("true", sink)), Request(model), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(handler.Methods);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Theory]
    [InlineData("gemini-test", false)]
    [InlineData("antigravity-preview", false)]
    [InlineData("deep-research-preview", true)]
    public async Task Sink_failures_propagate_including_best_effort_cleanup(string model, bool cleanup)
    {
        var failure = new IOException("debug sink failed");
        var sink = new Sink((e, _) => e.Kind == (cleanup ? "response-body" : "response-event")
            && (!cleanup || e.Payload.Content == "cleanup") ? ValueTask.FromException(failure) : ValueTask.CompletedTask);
        var handler = new Handler(cleanup ? [Json(Pending), Json(Completed), Json("cleanup")] : [Sse(Native)]);
        var provider = Provider(handler, Events("true", sink));
        var thrown = await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (cleanup) await provider.GetInteraction(Request(model));
            else await Read(provider, Request(model));
        });
        Assert.Same(failure, thrown);
        Assert.Equal(cleanup ? 3 : 1, handler.Methods.Count);
    }

    [Theory]
    [InlineData("gemini-test")]
    [InlineData("antigravity-preview")]
    [InlineData("agents/custom")]
    public async Task Existing_UI_writer_receives_each_debug_event_exactly_once(string model)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[RequestDebugEvents.HeaderName] = "true";
        using var body = new MemoryStream();
        context.Response.Body = body;
        var sink = new Sink();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton<IProviderDebugSink>(sink);
        services.AddProviderDebugEvents();
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        context.RequestServices = scope.ServiceProvider;
        var provider = Provider(new Handler([Sse(Native)]), scope.ServiceProvider.GetRequiredService<IProviderDebugEmitter>());
        using (var writer = new ChatSseWriter(context))
            await foreach (var _ in provider.StreamUnifiedAsync(new AIRequest
            {
                Model = model, ProviderId = "google", Input = new AIInput { Text = "Hello" }
            })) { }

        var parts = Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, parts.Length);
        Assert.Equal(sink.Items.Count, parts.Length);
        for (var index = 0; index < parts.Length; index++)
        {
            using var part = JsonDocument.Parse(parts[index]["data: ".Length..]);
            Assert.Equal(ChatSseWriter.DebugPartType, part.RootElement.GetProperty("type").GetString());
            Assert.True(part.RootElement.GetProperty("transient").GetBoolean());
            Assert.Equal(JsonSerializer.SerializeToElement(sink.Items[index], JsonSerializerOptions.Web).GetRawText(),
                part.RootElement.GetProperty("data").GetRawText());
        }
    }

    private static InteractionRequest Request(string model) => new()
    {
        Model = model, Input = new InteractionsInput("Hello 🌍"),
        AdditionalProperties = new() { ["debug"] = JsonSerializer.SerializeToElement(true) }
    };
    private static GoogleAIProvider Provider(Handler handler, IProviderDebugEmitter? debug = null)
        => new(new Keys(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GoogleAIProvider>.Instance, new Factory(new HttpClient(handler)), debug);
    private static RequestDebugEvents Events(string? header, Sink sink)
    {
        var context = new DefaultHttpContext();
        if (header is not null) context.Request.Headers[RequestDebugEvents.HeaderName] = header;
        return new(new HttpContextAccessor { HttpContext = context }, [sink]);
    }
    private static async Task<List<InteractionStreamEventPart>> Read(GoogleAIProvider provider, InteractionRequest request, CancellationToken ct = default)
    {
        var result = new List<InteractionStreamEventPart>();
        await foreach (var part in provider.GetInteractions(request, ct)) result.Add(part);
        return result;
    }
    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string content)
        => new(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(content))
            { Headers = { ContentType = new("text/event-stream") } } };
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "secret-api-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Sink(Func<ProviderDebugEvent, CancellationToken, ValueTask>? onWrite = null) : IProviderDebugSink
    {
        public List<ProviderDebugEvent> Items { get; } = [];
        public async ValueTask WriteAsync(ProviderDebugEvent e, CancellationToken ct)
        {
            if (onWrite is not null) await onWrite(e, ct);
            Items.Add(e);
        }
    }
    private sealed class Handler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<string?> Bodies { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return responses.Dequeue();
        }
    }
    private sealed class FragmentedStream(string content, int fragmentSize = 1) : MemoryStream(Encoding.UTF8.GetBytes(content))
    {
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], ct);
            BytesRead += count;
            return count;
        }
    }
}
