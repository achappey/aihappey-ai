using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Http;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace AIHappey.Tests.Diagnostics;

public sealed class ProviderDebugTests
{
    private const string NativeStream = ": native keepalive\r\n\r\nevent: future.event\r\ndata: {\"type\":\"future.event\",\"native\":true}\r\n\r\n"
        + "event: message.output.delta\r\ndata: {\"type\":\"message.output.delta\",\"content\":\"Héllo 🌍\"}\r\n\r\n"
        + "event: conversation.response.done\r\ndata: {\"type\":\"conversation.response.done\",\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}\r\n\r\n: eof tail";

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("invalid", false)]
    [InlineData("1", false)]
    [InlineData("true,false", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void Header_requires_a_valid_true_boolean(string? value, bool expected)
    {
        var context = Context(value);
        Assert.Equal(expected, RequestDebugEvents.IsEnabled(context.Request.Headers));
        context.Request.Headers[RequestDebugEvents.HeaderName] = new StringValues(["true", "true"]);
        Assert.False(RequestDebugEvents.IsEnabled(context.Request.Headers));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("invalid")]
    public async Task Disabled_requests_emit_nothing(string? header)
    {
        var sink = new RecordingSink();
        var events = Events(Context(header), sink);
        await Emit(events);
        Assert.Empty(sink.Items);
    }

    [Fact]
    public async Task Concurrent_emissions_are_ordered_and_subscriptions_receive_the_same_objects()
    {
        var host = new RecordingSink();
        var chat = new RecordingSink();
        var events = Events(Context("true"), host);
        using (events.Subscribe(chat))
            await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Emit(events).AsTask()));

        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), host.Items.Select(e => e.Sequence));
        Assert.Equal(host.Items.Count, chat.Items.Count);
        for (var i = 0; i < chat.Items.Count; i++)
            Assert.Same(host.Items[i], chat.Items[i]);

        await Emit(events);
        Assert.Equal(40, chat.Items.Count);
        Assert.Equal(41, host.Items.Count);
    }

    [Fact]
    public async Task Scoped_registration_is_isolated_and_console_requires_explicit_host_opt_in()
    {
        var services = new ServiceCollection();
        services.AddProviderDebugEvents();
        services.AddProviderDebugEvents();
        using var root = services.BuildServiceProvider();
        var accessor = root.GetRequiredService<IHttpContextAccessor>();
        Assert.Empty(root.GetServices<IProviderDebugSink>());

        accessor.HttpContext = Context("true");
        using var first = root.CreateScope();
        var firstEvents = first.ServiceProvider.GetRequiredService<RequestDebugEvents>();
        Assert.Same(firstEvents, first.ServiceProvider.GetRequiredService<IProviderDebugEmitter>());
        var firstSink = new RecordingSink();
        using var subscription = firstEvents.Subscribe(firstSink);

        accessor.HttpContext = Context("false");
        using var second = root.CreateScope();
        var secondEvents = second.ServiceProvider.GetRequiredService<RequestDebugEvents>();
        Assert.NotSame(firstEvents, secondEvents);
        await Task.WhenAll(Emit(firstEvents).AsTask(), Emit(secondEvents).AsTask());
        Assert.Single(firstSink.Items);
        Assert.False(secondEvents.Enabled);
        Assert.NotEqual(firstEvents.RequestId, secondEvents.RequestId);

        services.AddProviderDebugConsole();
        services.AddProviderDebugConsole();
        using var windows = services.BuildServiceProvider();
        Assert.IsType<ConsoleProviderDebugSink>(Assert.Single(windows.GetServices<IProviderDebugSink>()));
    }

    [Fact]
    public async Task Chat_debug_is_flushed_immediately_with_normal_parts_and_unsubscribed_on_dispose()
    {
        var context = Context("true");
        using var body = new FlushTrackingStream();
        context.Response.Body = body;
        var hostSink = new RecordingSink();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton<IProviderDebugSink>(hostSink);
        services.AddProviderDebugEvents();
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        context.RequestServices = scope.ServiceProvider;
        var events = scope.ServiceProvider.GetRequiredService<IProviderDebugEmitter>();

        using (var writer = new ChatSseWriter(context))
        {
            await Emit(events);
            Assert.Equal(1, body.Flushes); // No mapped normal output or stream completion needed.
            await writer.WriteAsync(new TextDeltaUIMessageStreamPart { Id = "text", Delta = "normal" });
            await Task.WhenAll(Emit(events).AsTask(), writer.WriteAsync(new TextDeltaUIMessageStreamPart { Id = "text", Delta = "next" }).AsTask());
        }

        var frames = Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, frames.Length);
        var parts = frames.Select(f => JsonDocument.Parse(f["data: ".Length..])).ToArray();
        try
        {
            var debug = parts.Where(p => p.RootElement.GetProperty("type").GetString() == ChatSseWriter.DebugPartType).ToArray();
            Assert.Equal(2, debug.Length);
            Assert.True(debug[0].RootElement.GetProperty("transient").GetBoolean());
            Assert.Equal(JsonSerializer.SerializeToElement(hostSink.Items[0], JsonSerializerOptions.Web).GetRawText(),
                debug[0].RootElement.GetProperty("data").GetRawText());
        }
        finally
        {
            foreach (var part in parts) part.Dispose();
        }

        var length = body.Length;
        await Emit(events);
        Assert.Equal(length, body.Length);
        Assert.Equal(4, body.Flushes);
    }

    [Fact]
    public async Task Raw_reads_preserve_split_utf8_comments_markers_and_malformed_content_without_prereading()
    {
        var bytes = Encoding.UTF8.GetBytes(": comment\r\nevent: unknown\r\ndata: {bad}\r\n\r\ndata: [DONE]\n\n🌍tail");
        using var native = new FragmentedStream(bytes, 1);
        var sink = new RecordingSink();
        var events = Events(Context("true"), sink);
        using var observed = new DebugResponseStream(native, events, "any-provider", "/native", "op", "text/event-stream");
        Assert.Equal(0, native.Reads);
        var buffer = new byte[16];
        while (await observed.ReadAsync(buffer.AsMemory()) > 0) { }
        Assert.Equal(bytes, Decode(sink.Items));
        Assert.All(sink.Items, e => Assert.Equal("response-event", e.Kind));
        Assert.All(sink.Items, e => Assert.Equal("text", e.Payload.Encoding));
        Assert.Equal("unknown", sink.Items[0].Payload.EventName);
        Assert.Equal("{bad}", sink.Items[0].Payload.Data?.GetString());
        Assert.Equal("[DONE]", sink.Items[1].Payload.Data?.GetString());
        Assert.Equal("🌍tail", sink.Items[2].Payload.Content);
        Assert.False(sink.Items[2].Payload.Complete);
    }

    [Fact]
    public async Task Delivery_applies_backpressure_and_cancellation_without_consuming_later_chunks()
    {
        var firstFrame = Encoding.UTF8.GetBytes("data: {\"native\":true}\n\n");
        using var native = new FragmentedStream([.. firstFrame, .. Encoding.UTF8.GetBytes("data: later\n\n")], firstFrame.Length);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = Events(Context("true"), new BlockingSink(entered));
        using var observed = new DebugResponseStream(native, events, "provider", "/native", "op", "text/event-stream");
        using var cancellation = new CancellationTokenSource();
        var read = observed.ReadAsync(new byte[128].AsMemory(), cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(read.IsCompleted);
        Assert.Equal(1, native.Reads);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(1, native.Reads);
    }

    [Fact]
    public async Task Mistral_emits_exact_upstream_request_and_stream_not_gateway_body_and_preserves_normal_output()
    {
        var sink = new RecordingSink();
        var events = Events(Context("true"), sink);
        var handler = new MistralHandler(Encoding.UTF8.GetBytes(NativeStream));
        var provider = Provider(handler, events);
        var normal = await Collect(provider.StreamUnifiedAsync(Request()));
        var baseline = await Collect(Provider(new MistralHandler(Encoding.UTF8.GetBytes(NativeStream))).StreamUnifiedAsync(Request()));

        var request = Assert.Single(sink.Items.Where(e => e.Kind == "request-body"));
        Assert.Equal(handler.SentBody, request.Payload.Content);
        Assert.Equal("/v1/conversations", handler.SentUri?.AbsolutePath);
        Assert.Equal("mistral", request.Provider);
        Assert.Equal("/v1/conversations", request.Operation);
        Assert.Contains("\"stream\":true", request.Payload.Content);
        Assert.True(request.Payload.Data?.GetProperty("stream").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetBytes(NativeStream), Decode(sink.Items));
        var delta = Assert.Single(sink.Items.Where(e => e.Payload.EventName == "message.output.delta"));
        Assert.Equal("Héllo 🌍", delta.Payload.Data?.GetProperty("content").GetString());
        Assert.All(sink.Items, e => Assert.Equal(request.OperationId, e.OperationId));
        Assert.Equal(baseline.Select(e => e.Event.Type), normal.Select(e => e.Event.Type));
        Assert.Equal(baseline.Select(e => e.Event.Data).OfType<AITextDeltaEventData>().Select(d => d.Delta),
            normal.Select(e => e.Event.Data).OfType<AITextDeltaEventData>().Select(d => d.Delta));
        Assert.DoesNotContain(normal, e => e.Event.Type.StartsWith("data-"));
        Assert.True(handler.Native.Disposed);
    }

    [Fact]
    public async Task Mistral_captures_malformed_native_chunk_before_parser_throws()
    {
        const string malformed = "event: message.output.delta\ndata: {malformed}\n\n";
        var sink = new RecordingSink();
        var handler = new MistralHandler(Encoding.UTF8.GetBytes(malformed));
        await Assert.ThrowsAnyAsync<Exception>(() => Collect(Provider(handler, Events(Context("true"), sink)).StreamUnifiedAsync(Request())));
        Assert.Equal(Encoding.UTF8.GetBytes(malformed), Decode(sink.Items));
        Assert.True(handler.Native.Disposed);
    }

    [Fact]
    public async Task Mistral_nonstream_and_http_error_capture_full_native_bodies()
    {
        const string response = "{\"outputs\":[{\"type\":\"message.output\",\"role\":\"assistant\",\"content\":\"Hello\"}]}";
        var sink = new RecordingSink();
        var handler = new MistralHandler(Encoding.UTF8.GetBytes(response));
        await Provider(handler, Events(Context("true"), sink)).ExecuteUnifiedAsync(Request());
        Assert.Equal(handler.SentBody, sink.Items[0].Payload.Content);
        Assert.Equal(response, sink.Items[1].Payload.Content);
        Assert.Equal("response-body", sink.Items[1].Kind);

        var errorSink = new RecordingSink();
        var errorHandler = new MistralHandler(Encoding.UTF8.GetBytes("native error"), HttpStatusCode.BadRequest);
        await Assert.ThrowsAnyAsync<Exception>(() => Collect(Provider(errorHandler, Events(Context("true"), errorSink)).StreamUnifiedAsync(Request())));
        Assert.Equal("native error", errorSink.Items[1].Payload.Content);
    }

    private static DefaultHttpContext Context(string? header)
    {
        var context = new DefaultHttpContext { TraceIdentifier = Guid.NewGuid().ToString("n") };
        if (header is not null) context.Request.Headers[RequestDebugEvents.HeaderName] = header;
        return context;
    }

    private static RequestDebugEvents Events(HttpContext context, IProviderDebugSink sink)
        => new(new HttpContextAccessor { HttpContext = context }, [sink]);
    private static ValueTask Emit(IProviderDebugEmitter events)
        => events.EmitAsync("provider", "/operation", "operation-id", "request-body", new("raw", "text", "application/json"));
    private static byte[] Decode(IEnumerable<ProviderDebugEvent> events)
        => Encoding.UTF8.GetBytes(string.Concat(events.Where(e => e.Kind == "response-event").Select(e => e.Payload.Content)));
    private static AIRequest Request() => new() { ProviderId = "mistral", Model = "mistral-small-latest", Input = new AIInput { Text = "upstream prompt" } };
    private static async Task<List<AIStreamEvent>> Collect(IAsyncEnumerable<AIStreamEvent> stream)
    {
        var events = new List<AIStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }
    private static MistralProvider Provider(MistralHandler handler, IProviderDebugEmitter? events = null)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(handler)), events);

    private sealed class RecordingSink : IProviderDebugSink
    {
        public List<ProviderDebugEvent> Items { get; } = [];
        public ValueTask WriteAsync(ProviderDebugEvent debugEvent, CancellationToken cancellationToken)
        {
            Items.Add(debugEvent);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class BlockingSink(TaskCompletionSource entered) : IProviderDebugSink
    {
        public async ValueTask WriteAsync(ProviderDebugEvent debugEvent, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
    private sealed class FlushTrackingStream : MemoryStream
    {
        public int Flushes { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken) { Flushes++; return Task.CompletedTask; }
    }
    private sealed class FragmentedStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(fragmentSize, buffer.Length)], cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "secret-not-in-debug";
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class MistralHandler(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? SentBody { get; private set; }
        public Uri? SentUri { get; private set; }
        public FragmentedStream Native { get; } = new(bytes, 7);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            SentUri = request.RequestUri;
            var content = new StreamContent(Native);
            content.Headers.ContentType = new("text/event-stream");
            return new HttpResponseMessage(status) { Content = content };
        }
    }
}
