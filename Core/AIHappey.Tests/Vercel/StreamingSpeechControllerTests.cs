using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using AzureController = AIHappey.AzureAuth.Controllers.StreamingSpeechController;
using HeaderController = AIHappey.HeaderAuth.Controllers.StreamingSpeechController;

namespace AIHappey.Tests.Vercel;

public sealed class StreamingSpeechControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolved_model_is_normalized_and_unsupported_stream_is_an_sse_error(bool azure)
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var recording = (RecordingProvider)(object)provider;
        const string errorMessage = "Provider 'example' does not support this speech stream.";
        recording.OnSpeechStreaming = (_, _) => throw new NotSupportedException(errorMessage);
        var resolver = new RecordingResolver(provider);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        await using var body = new CountingStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;
        resolver.OnResolve = () => Assert.Equal(!azure, context.Items.ContainsKey("AIHappey.HeaderAuth.ActiveProvider"));
        var request = Request();
        using var cancellation = new CancellationTokenSource();

        await Post(azure, resolver, context, request, cancellation.Token);

        Assert.Equal("example/folder/tts", resolver.ResolvedModel);
        Assert.Equal(cancellation.Token, resolver.CancellationToken);
        Assert.Equal("folder/tts", request.Model);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var sse = Encoding.UTF8.GetString(body.ToArray());
        Assert.StartsWith("data: ", sse);
        Assert.EndsWith("\n\n", sse);
        using var json = JsonDocument.Parse(sse["data: ".Length..].Trim());
        Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(errorMessage, json.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("[DONE]", sse);
        Assert.DoesNotContain("\"type\":\"finish\"", sse);
        Assert.True(body.FlushCount > 0);
        Assert.Same(request, recording.SpeechRequest);
        Assert.Equal(cancellation.Token, recording.SpeechCancellationToken);
        Assert.Equal([nameof(IModelProvider.SpeechStreamingAsync)], recording.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_models_return_json_bad_request_without_starting_sse(bool azure)
    {
        var resolver = new RecordingResolver(null);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        await using var body = new CountingStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;
        var request = Request();

        await Post(azure, resolver, context, request, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        using var json = JsonDocument.Parse(body.ToArray());
        Assert.Equal("Model 'example/folder/tts' is not available.", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("example/folder/tts", request.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_streams_do_not_emit_error_or_completion_events(bool azure)
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var resolver = new RecordingResolver(provider);
        await using var body = new CountingStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        using var cancellation = new CancellationTokenSource();
        resolver.OnResolve = cancellation.Cancel;

        await Post(azure, resolver, context, Request(), cancellation.Token);

        Assert.Empty(body.ToArray());
        Assert.Empty(((RecordingProvider)(object)provider).Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_streams_forward_provider_parts_without_synthesis_fallback_or_input_changes(bool azure)
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var recording = (RecordingProvider)(object)provider;
        StreamingSpeechPart[] parts =
        [
            new SpeechStreamStartPart { Warnings = [] },
            new SpeechResponseMetadataPart { ModelId = "example/folder/tts" },
            new SpeechAudioPart { Audio = "AQID", AudioFormat = new() { Type = "audio/pcm", Rate = 24000 } },
            new SpeechRawPart { RawValue = new { chunk = 1 } },
            new SpeechFinishPart { Usage = new() { InputCharacters = 5 } }
        ];
        recording.OnSpeechStreaming = (_, ct) => StreamParts(parts, ct);
        var resolver = new RecordingResolver(provider);
        await using var body = new CountingStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        using var cancellation = new CancellationTokenSource();
        var request = Request();
        request.Voice = "default";
        request.OutputFormat = new() { Type = "audio/pcm", Rate = 24000 };
        request.IncludeRawChunks = true;
        request.ProviderOptions = new() { ["example"] = JsonSerializer.SerializeToElement(new { seed = 42 }) };
        var expected = JsonSerializer.Deserialize<StreamingSpeechRequest>(JsonSerializer.Serialize(request))!;
        expected.Model = "folder/tts";

        await Post(azure, resolver, context, request, cancellation.Token);

        var expectedSse = string.Concat(parts.Select(part => $"data: {JsonSerializer.Serialize(part)}\n\n"))
            + "data: [DONE]\n\n";
        Assert.Equal(expectedSse, Encoding.UTF8.GetString(body.ToArray()));
        Assert.True(body.FlushCount >= parts.Length + 1);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(request));
        Assert.Same(request, recording.SpeechRequest);
        Assert.Equal(cancellation.Token, recording.SpeechCancellationToken);
        Assert.Equal([nameof(IModelProvider.SpeechStreamingAsync)], recording.Calls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Provider_failures_emit_an_sse_error_without_completion(bool azure, bool duringEnumeration, bool unsupported)
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var recording = (RecordingProvider)(object)provider;
        Exception error = unsupported
            ? new NotSupportedException("example does not support this stream")
            : new NotImplementedException("example streaming is not implemented");
        recording.OnSpeechStreaming = (_, _) => duringEnumeration ? FailAfterFirstPart(error) : throw error;
        var resolver = new RecordingResolver(provider);
        await using var body = new CountingStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await Post(azure, resolver, context, Request(), CancellationToken.None);

        var sse = Encoding.UTF8.GetString(body.ToArray());
        var frames = sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(duringEnumeration ? 2 : 1, frames.Length);
        if (duringEnumeration)
        {
            using var first = JsonDocument.Parse(frames[0]["data: ".Length..]);
            Assert.Equal("stream-start", first.RootElement.GetProperty("type").GetString());
        }
        using var last = JsonDocument.Parse(frames[^1]["data: ".Length..]);
        Assert.Equal("error", last.RootElement.GetProperty("type").GetString());
        Assert.Equal(error.Message, last.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("[DONE]", sse);
        Assert.DoesNotContain("\"type\":\"finish\"", sse);
        Assert.True(body.FlushCount >= frames.Length);
        Assert.Equal([nameof(IModelProvider.SpeechStreamingAsync)], recording.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pre_cancelled_requests_do_not_invoke_the_provider(bool azure)
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var resolver = new RecordingResolver(provider);
        await using var body = new CountingStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Post(azure, resolver, context, Request(), cancellation.Token);

        Assert.Empty(body.ToArray());
        Assert.Equal(0, body.FlushCount);
        Assert.Empty(((RecordingProvider)(object)provider).Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Provider_cancellation_does_not_emit_error_or_completion_events(bool azure, bool duringEnumeration)
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var recording = (RecordingProvider)(object)provider;
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        recording.OnSpeechStreaming = (_, ct) =>
        {
            if (duringEnumeration)
                return CancelAfterFirstPart(cancellation, () => disposed = true);
            cancellation.Cancel();
            throw new OperationCanceledException(ct);
        };
        var resolver = new RecordingResolver(provider);
        await using var body = new CountingStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await Post(azure, resolver, context, Request(), cancellation.Token);

        var sse = Encoding.UTF8.GetString(body.ToArray());
        if (duringEnumeration)
        {
            using var json = JsonDocument.Parse(sse["data: ".Length..].Trim());
            Assert.Equal("stream-start", json.RootElement.GetProperty("type").GetString());
            Assert.True(disposed);
        }
        else
            Assert.Empty(sse);
        Assert.DoesNotContain("\"type\":\"error\"", sse);
        Assert.DoesNotContain("\"type\":\"finish\"", sse);
        Assert.DoesNotContain("[DONE]", sse);
        if (duringEnumeration)
            Assert.True(body.FlushCount > 0);
        else
            Assert.Equal(0, body.FlushCount);
        Assert.Equal(cancellation.Token, recording.SpeechCancellationToken);
        Assert.Equal([nameof(IModelProvider.SpeechStreamingAsync)], recording.Calls);
    }

    [Fact]
    public void Both_routes_are_post_and_azure_requires_authorization()
    {
        foreach (var type in new[] { typeof(HeaderController), typeof(AzureController) })
        {
            Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
            Assert.Equal("api/speech/stream", type.GetCustomAttribute<RouteAttribute>()!.Template);
            var action = type.GetMethod("Post")!;
            Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(action.GetParameters()[0].GetCustomAttribute<FromBodyAttribute>());
        }
        Assert.NotNull(typeof(AzureController).GetMethod("Post")!.GetCustomAttribute<AuthorizeAttribute>());
    }

    private static Task Post(bool azure, IAIModelProviderResolver resolver, HttpContext context,
        StreamingSpeechRequest request, CancellationToken cancellationToken)
    {
        var controllerContext = new ControllerContext { HttpContext = context };
        return azure
            ? new AzureController(resolver) { ControllerContext = controllerContext }.Post(request, cancellationToken)
            : new HeaderController(resolver) { ControllerContext = controllerContext }.Post(request, cancellationToken);
    }

    private static StreamingSpeechRequest Request() => new()
    {
        Model = "example/folder/tts",
        Text = [new() { Text = "hel", Voice = "alice" }, new() { Text = "lo", Voice = "bob" }]
    };

    private static async IAsyncEnumerable<StreamingSpeechPart> StreamParts(
        IEnumerable<StreamingSpeechPart> parts, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return part;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StreamingSpeechPart> FailAfterFirstPart(Exception error)
    {
        yield return new SpeechStreamStartPart { Warnings = [] };
        await Task.CompletedTask;
        throw error;
    }

    private static async IAsyncEnumerable<StreamingSpeechPart> CancelAfterFirstPart(
        CancellationTokenSource cancellation, Action onDispose)
    {
        try
        {
            yield return new SpeechStreamStartPart { Warnings = [] };
            await Task.CompletedTask;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
        finally
        {
            onDispose();
        }
    }

    public class RecordingProvider : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public Func<StreamingSpeechRequest, CancellationToken, IAsyncEnumerable<StreamingSpeechPart>>? OnSpeechStreaming { get; set; }
        public StreamingSpeechRequest? SpeechRequest { get; private set; }
        public CancellationToken SpeechCancellationToken { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            if (targetMethod.Name == nameof(IModelProvider.SpeechStreamingAsync) && OnSpeechStreaming is not null)
            {
                SpeechRequest = (StreamingSpeechRequest)args![0]!;
                SpeechCancellationToken = (CancellationToken)args[1]!;
                return OnSpeechStreaming(SpeechRequest, SpeechCancellationToken);
            }
            throw new InvalidOperationException("Unexpected provider call: " + targetMethod.Name);
        }
    }

    private sealed class RecordingResolver(IModelProvider? provider) : IAIModelProviderResolver
    {
        public string? ResolvedModel { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public Action? OnResolve { get; set; }

        public Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
        {
            ResolvedModel = model;
            CancellationToken = ct;
            OnResolve?.Invoke();
            return Task.FromResult(provider!);
        }

        public IModelProvider GetProvider() => provider!;
        public Task<ModelResponse> ResolveModels(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
