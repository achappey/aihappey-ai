using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Core.Providers.Notte;
using AIHappey.Core.Providers.ShadowOS;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Diagnostics;

public sealed class AdditionalProviderDebugTests
{
    [Theory]
    [InlineData("notte", false)]
    [InlineData("notte", true)]
    [InlineData("cohere", false)]
    [InlineData("cohere", true)]
    [InlineData("shadowos", false)]
    [InlineData("shadowos", true)]
    public async Task Bodies_are_exact_correlated_and_opt_in(string provider, bool enabled)
    {
        var sink = new Sink();
        var native = Success(provider);
        using var handler = new Handler(_ => Response(native));
        using var client = new HttpClient(handler);
        var result = await Execute(provider, client, Events(enabled, sink), Request(provider));
        Assert.Equal("completed", result.Status);
        if (!enabled)
        {
            Assert.Empty(sink.Items);
            return;
        }
        var sent = Assert.Single(handler.Calls, c => c.Body is not null);
        Assert.Equal(2, sink.Items.Count);
        var request = sink.Items[0];
        var response = sink.Items[1];
        Assert.Equal("request-body", request.Kind);
        Assert.Equal(sent.Body, request.Payload.Content);
        Assert.Equal("response-body", response.Kind);
        Assert.Equal(native, response.Payload.Content);
        Assert.Equal(request.OperationId, response.OperationId);
        Assert.NotEmpty(request.OperationId);
        Assert.All(sink.Items, e =>
        {
            Assert.Equal(provider, e.Provider);
            Assert.Equal("test-request", e.RequestId);
            Assert.Equal("text", e.Payload.Encoding);
            Assert.DoesNotContain("secret-not-in-debug", e.Payload.Content);
        });
        Assert.Equal(new long[] { 1, 2 }, sink.Items.Select(e => e.Sequence));
    }

    [Theory]
    [InlineData("notte")]
    [InlineData("cohere")]
    [InlineData("shadowos")]
    public async Task Direct_construction_without_an_emitter_still_works(string provider)
    {
        using var handler = new Handler(_ => Response(Success(provider)));
        using var client = new HttpClient(handler);
        Assert.Equal("completed", (await Execute(provider, client, null, Request(provider))).Status);
    }

    [Theory]
    [InlineData("notte", false)]
    [InlineData("notte", true)]
    [InlineData("cohere", false)]
    [InlineData("cohere", true)]
    [InlineData("shadowos", false)]
    [InlineData("shadowos", true)]
    public async Task Errors_are_captured_before_normal_error_handling(string provider, bool streaming)
    {
        const string native = "native error\r\n with whitespace ";
        var sink = new Sink();
        using var handler = new Handler(_ => Response(native, HttpStatusCode.BadRequest, "text/plain"));
        using var client = new HttpClient(handler);
        if (provider == "cohere" && streaming)
            Assert.Contains(await Stream(provider, client, Events(true, sink), Request(provider)), e => e.Event.Type == "error");
        else
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                if (streaming) await Stream(provider, client, Events(true, sink), Request(provider));
                else await Execute(provider, client, Events(true, sink), Request(provider));
            });
        var body = Assert.Single(sink.Items, e => e.Kind == "response-body");
        Assert.Equal(native, body.Payload.Content);
        Assert.Equal("text/plain", body.Payload.MediaType);
        Assert.Equal(sink.Items[0].OperationId, body.OperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notte_emits_every_authentication_and_agent_poll_before_snapshot_replacement(bool streaming)
    {
        var sink = new Sink();
        var auth = 0;
        var agent = 0;
        var nativeBodies = new List<string>();
        using var handler = new Handler(request =>
        {
            // Previous responses must already be delivered, not held until completion.
            Assert.Equal(nativeBodies.Count, sink.Items.Count(e => e.Kind == "response-body"));
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/sessions/start" => "{\"session_id\":\"s1\",\"status\":\"authenticating\"}",
                "/sessions/s1/auth" => ++auth < 3 ? $"{{\"status\":\"authenticating\",\"poll\":{auth}}}" : "{\"status\":\"active\"}",
                "/agents/start" => "{\"agent_id\":\"a1\"}",
                "/agents/a1" => ++agent < 4 ? $"{{\"status\":\"active\",\"poll\":{agent}}}" : "{\"status\":\"closed\",\"answer\":\"done\"}",
                "/sessions/s1/files" => "{\"files\":[],\"total\":0}",
                _ => throw new InvalidOperationException("Unexpected request")
            };
            nativeBodies.Add(body);
            return Response(body);
        });
        using var client = new HttpClient(handler);
        var request = new AIRequest
        {
            ProviderId = "notte", Model = "agent", Input = new() { Text = "task" },
            Metadata = new() { ["notte"] = new { gateway = new { poll_seconds = 0.01, wait_seconds = 10 } } }
        };
        var emitter = Events(true, sink);
        if (streaming) await Stream("notte", client, emitter, request);
        else await Execute("notte", client, emitter, request);
        Assert.Equal(3, auth);
        Assert.Equal(4, agent);
        var responses = sink.Items.Where(e => e.Kind == "response-body").ToArray();
        Assert.Equal(nativeBodies, responses.Select(e => e.Payload.Content));
        Assert.Equal(10, responses.Length);
        Assert.Equal(responses.Length, responses.Select(e => e.OperationId).Distinct().Count());
        foreach (var call in handler.Calls.Where(c => c.Body is not null))
        {
            var debugRequest = Assert.Single(sink.Items, e => e.Kind == "request-body" && e.Operation == call.Path.TrimStart('/'));
            Assert.Equal(call.Body, debugRequest.Payload.Content);
            Assert.Single(responses, e => e.OperationId == debugRequest.OperationId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohere_observes_native_sse_including_unknown_comments_and_malformed_frames(bool malformed)
    {
        var native = ": keepalive\r\n\r\nevent: future\r\ndata: {\"type\":\"future\",\"text\":\"Héllo 🌍\"}\r\n\r\n"
            + (malformed ? "data: {bad}\n\n" : "data: [DONE]\n\n: tail");
        var sink = new Sink();
        using var handler = new Handler(_ =>
        {
            var content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(native)));
            content.Headers.ContentType = new("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        if (malformed)
            await Assert.ThrowsAnyAsync<JsonException>(() => Stream("cohere", client, Events(true, sink), Request("cohere")));
        else
            Assert.Contains(await Stream("cohere", client, Events(true, sink), Request("cohere")), e => e.Event.Type == "finish");
        var request = Assert.Single(sink.Items, e => e.Kind == "request-body");
        Assert.Equal(Assert.Single(handler.Calls, c => c.Body is not null).Body, request.Payload.Content);
        Assert.True(request.Payload.Data?.GetProperty("stream").GetBoolean());
        Assert.Equal(native, string.Concat(sink.Items.Where(e => e.Kind == "response-event").Select(e => e.Payload.Content)));
        Assert.All(sink.Items, e => Assert.Equal(request.OperationId, e.OperationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohere_parse_captures_each_document_even_when_adapted_to_streaming(bool streaming)
    {
        const string native = "{ \"pages\": [{\"markdown\":{\"content\":\"parsed\"}}],\"finish_reason\":\"COMPLETE\" }";
        var sink = new Sink();
        using var handler = new Handler(_ => Response(native));
        using var client = new HttpClient(handler);
        var request = new AIRequest { ProviderId = "cohere", Model = "parse-v1",
            Input = new() { Items = [new() { Role = "user", Content =
            [new AIFileContentPart { Type = "file", Data = "https://example.test/1.png" },
             new AIFileContentPart { Type = "file", Data = "https://example.test/2.png" }] }] } };
        if (streaming) await Stream("cohere", client, Events(true, sink), request);
        else await Execute("cohere", client, Events(true, sink), request);
        Assert.Equal(4, sink.Items.Count);
        Assert.Equal(2, sink.Items.Select(e => e.OperationId).Distinct().Count());
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(handler.Calls[i].Body, sink.Items[i * 2].Payload.Content);
            Assert.Equal(native, sink.Items[i * 2 + 1].Payload.Content);
            Assert.Equal(sink.Items[i * 2].OperationId, sink.Items[i * 2 + 1].OperationId);
        }
        Assert.All(sink.Items, e => Assert.Equal("v2/parse", e.Operation));
    }

    [Theory]
    [InlineData("notte")]
    [InlineData("cohere")]
    [InlineData("shadowos")]
    public async Task Sink_failures_propagate_before_upstream_submission(string provider)
    {
        using var handler = new Handler(_ => Response(Success(provider)));
        using var client = new HttpClient(handler);
        var sink = new Sink { OnWrite = (_, _) => throw new IOException("debug sink failed") };
        var error = await Assert.ThrowsAsync<IOException>(() => Execute(provider, client, Events(true, sink), Request(provider)));
        Assert.Equal("debug sink failed", error.Message);
        Assert.DoesNotContain(handler.Calls, c => c.Body is not null);
    }

    [Theory]
    [InlineData("notte")]
    [InlineData("cohere")]
    [InlineData("shadowos")]
    public async Task Cancellation_interrupts_debug_backpressure_before_submission(string provider)
    {
        using var handler = new Handler(_ => Response(Success(provider)));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink { OnWrite = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        } };
        var execution = Execute(provider, client, Events(true, sink), Request(provider), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.DoesNotContain(handler.Calls, c => c.Body is not null);
    }

    private static string Success(string provider) => provider switch
    {
        "notte" => "{ \"answer\":\"done\",\"results\":[] }",
        "cohere" => "{ \"id\":\"c1\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}]},\"finish_reason\":\"COMPLETE\" }",
        _ => "{ \"session_id\":\"s1\",\"answer\":\"done\",\"files\":[] }"
    };
    private static AIRequest Request(string provider, string? model = null) => new()
    {
        ProviderId = provider, Model = model ?? (provider == "cohere" ? "command-test" : provider == "notte" ? "search" : "agent"),
        Input = new() { Text = "upstream prompt 🌍" }
    };
    private static RequestDebugEvents Events(bool enabled, Sink sink)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "test-request" };
        context.Request.Headers[RequestDebugEvents.HeaderName] = enabled ? "true" : "false";
        return new(new HttpContextAccessor { HttpContext = context }, [sink]);
    }
    private static Task<AIResponse> Execute(string provider, HttpClient client, IProviderDebugEmitter? debug, AIRequest request, CancellationToken ct = default)
        => provider switch
        {
            "notte" => new NotteProvider(new KeyResolver(), Cache(), new Factory(client), debug).ExecuteUnifiedAsync(request, ct),
            "cohere" => new CohereProvider(new KeyResolver(), Cache(), new Factory(client), debug).ExecuteUnifiedAsync(request, ct),
            _ => new ShadowOSProvider(new KeyResolver(), Cache(), new Factory(client), debug).ExecuteUnifiedAsync(request, ct)
        };
    private static async Task<List<AIStreamEvent>> Stream(string provider, HttpClient client, IProviderDebugEmitter debug, AIRequest request)
    {
        var stream = provider switch
        {
            "notte" => new NotteProvider(new KeyResolver(), Cache(), new Factory(client), debug).StreamUnifiedAsync(request),
            "cohere" => new CohereProvider(new KeyResolver(), Cache(), new Factory(client), debug).StreamUnifiedAsync(request),
            _ => new ShadowOSProvider(new KeyResolver(), Cache(), new Factory(client), debug).StreamUnifiedAsync(request)
        };
        var result = new List<AIStreamEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }
    private static AsyncCacheHelper Cache() => new(new MemoryCache(new MemoryCacheOptions()));
    private static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
        => new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "secret-not-in-debug";
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
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string Path, string? Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((path, body));
            // Model discovery is intentionally not instrumented.
            return path == "/v1/models" ? Response("{\"models\":[{\"name\":\"command-test\"}]}") : responder(request);
        }
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }
}
