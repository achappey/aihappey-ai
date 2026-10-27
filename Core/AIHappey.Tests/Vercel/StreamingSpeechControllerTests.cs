using System.Reflection;
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
        Assert.Contains("example", json.RootElement.GetProperty("error").GetString());
        Assert.Contains("does not support Vercel speech streaming yet", json.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("[DONE]", sse);
        Assert.DoesNotContain("\"type\":\"finish\"", sse);
        Assert.True(body.FlushCount > 0);
        Assert.Equal([nameof(IModelProvider.GetIdentifier)], recording.Calls);
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

    [Fact]
    public void Extension_stub_does_not_call_existing_synthesis_methods_or_modify_input()
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var request = Request();
        var before = JsonSerializer.Serialize(request);

        var error = Assert.Throws<NotSupportedException>(() => provider.SpeechStreamingAsync(request));

        Assert.Contains("example", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal([nameof(IModelProvider.GetIdentifier)], ((RecordingProvider)(object)provider).Calls);
    }

    [Fact]
    public void Extension_stub_honors_pre_cancelled_tokens_before_reporting_unsupported()
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => provider.SpeechStreamingAsync(Request(), cancellation.Token));
        Assert.Empty(((RecordingProvider)(object)provider).Calls);
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

    public class RecordingProvider : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return targetMethod.Name == nameof(IModelProvider.GetIdentifier)
                ? "example"
                : throw new InvalidOperationException("Unexpected provider call: " + targetMethod.Name);
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
