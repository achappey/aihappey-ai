using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.WellSaid;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.WellSaid;

public sealed class WellSaidProviderTests
{
    private const string Avatars = """
        {"avatars":[
          {"id":3,"name":"Voice One","style":"Narration","locale":"en_US","characteristics":["warm"]},
          {"id":12,"name":"Voice Two","language":"English","source":"native"},
          {"id":3,"name":"Duplicate"}
        ]}
        """;

    [Fact]
    public async Task DiscoveryPreservesNativeIdsAndMetadata()
    {
        var provider = Create((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal("https://api.wellsaidlabs.com/v1/tts/avatars", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            return Task.FromResult(Json(Avatars));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["wellsaid/3", "wellsaid/12"], models.Select(model => model.Id));
        Assert.Equal(["Voice One", "Voice Two"], models.Select(model => model.Name));
        Assert.All(models, model =>
        {
            Assert.Equal("speech", model.Type);
            Assert.Equal("WellSaid", model.OwnedBy);
            Assert.Null(model.Pricing);
            Assert.Null(model.ContextWindow);
        });
        var metadata = models[0].Metadata!["wellsaid"];
        Assert.Equal(3, metadata.GetProperty("id").GetInt32());
        Assert.Equal("en_US", metadata.GetProperty("locale").GetString());
        Assert.Equal("warm", metadata.GetProperty("characteristics")[0].GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"avatars\":{}}")]
    [InlineData("{\"avatars\":[null]}")]
    [InlineData("{\"avatars\":[{\"speaker_id\":3}]}")]
    [InlineData("{\"avatars\":[{\"id\":\"3\"}]}")]
    [InlineData("{\"avatars\":[{\"id\":1.5}]}")]
    public async Task MalformedDiscoveryDoesNotInventAvatarIds(string body)
        => await Assert.ThrowsAsync<JsonException>(() => Create((_, _) => Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task EmptyCatalogRemainsEmpty()
        => Assert.Empty(await Create((_, _) => Task.FromResult(Json("""{"avatars":[]}"""))).ListModels());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeySkipsDiscoveryAndRejectsExecution(string? key)
    {
        var provider = Create(NoNetwork, key);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
    }

    [Fact]
    public async Task StandardMappingAndNativeOptionsArePreservedWithoutMutation()
    {
        var native = Element(new
        {
            text = "not the input", speaker_id = 100, model = "caruso",
            future_option = new { expressive = true }
        });
        var original = native.GetRawText();
        var request = Request();
        request.Voice = "12";
        request.OutputFormat = "mp3";
        request.ProviderOptions = new() { ["wellsaid"] = native, ["other"] = Element(new { unrelated = true }) };
        var provider = Create(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.wellsaidlabs.com/v1/tts/stream", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            Assert.Equal("audio/mpeg", Assert.Single(message.Headers.Accept).MediaType);
            var body = await Body(message, token);
            Assert.Equal("Hello", body.GetProperty("text").GetString());
            Assert.Equal(3, body.GetProperty("speaker_id").GetInt32());
            Assert.Equal("caruso", body.GetProperty("model").GetString());
            Assert.True(body.GetProperty("future_option").GetProperty("expressive").GetBoolean());
            Assert.False(body.TryGetProperty("unrelated", out _));
            Assert.False(body.TryGetProperty("output_format", out _));
            return Audio([1, 2, 3]);
        });
        var response = await provider.SpeechRequest(request);
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("mp3", response.Audio.Format);
        Assert.Equal("wellsaid/3", response.Response.ModelId);
        Assert.Empty(response.Warnings);
        Assert.Equal("caruso", Assert.IsType<JsonElement>(response.Request!.Body).GetProperty("model").GetString());
        Assert.Equal(original, request.ProviderOptions["wellsaid"].GetRawText());
    }

    [Fact]
    public async Task MinimalRequestDoesNotForceNativeModelOrDefaults()
    {
        var provider = Create(async (message, token) =>
        {
            var body = await Body(message, token);
            Assert.Equal(2, body.EnumerateObject().Count());
            Assert.Equal(3, body.GetProperty("speaker_id").GetInt32());
            return Audio([1]);
        });
        var result = await provider.OpenAISpeechRequestAsync(new() { Model = "wellsaid/3", Input = "Hello" });
        Assert.Equal(new byte[] { 1 }, result.Audio);
        Assert.Equal("audio/mpeg", result.MimeType);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, 1f)]
    [InlineData(true, null)]
    [InlineData(true, 1f)]
    public async Task OpenAINamedVoiceUsesModelAvatarAndPreservesNativeOptions(bool streaming, float? speed)
    {
        var native = Element(new { speaker_id = 12, model = "caruso", audio_configs = new { custom = true } });
        var original = native.GetRawText();
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message);
            var body = await Body(message, token);
            Assert.Equal(3, body.GetProperty("speaker_id").GetInt32());
            Assert.Equal("Hello", body.GetProperty("text").GetString());
            Assert.Equal("caruso", body.GetProperty("model").GetString());
            Assert.True(body.GetProperty("audio_configs").GetProperty("custom").GetBoolean());
            Assert.False(body.TryGetProperty("voice", out _));
            Assert.False(body.TryGetProperty("speed", out _));
            return Audio([1, 2, 3]);
        });
        var request = new AudioSpeechRequest
        {
            Model = "wellsaid/3", Input = "Hello", Voice = "alloy", Speed = speed,
            ResponseFormat = "mp3", StreamFormat = streaming ? "sse" : null,
            AdditionalProperties = new() { ["providerOptions"] = Element(new { wellsaid = native }) }
        };
        if (streaming)
        {
            var events = new List<IAudioSpeechStreamEvent>();
            await foreach (var item in provider.OpenAISpeechStreamingAsync(request))
                events.Add(item);
            Assert.Equal(new byte[] { 1, 2, 3 },
                Convert.FromBase64String(Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio));
            Assert.Null(Assert.IsType<AudioSpeechStreamDone>(events[1]).Usage);
            Assert.Equal(2, events.Count);
        }
        else
        {
            var result = await provider.OpenAISpeechRequestAsync(request);
            Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio);
            Assert.Equal("audio/mpeg", result.MimeType);
        }
        Assert.Equal(original, request.AdditionalProperties["providerOptions"].GetProperty("wellsaid").GetRawText());
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.2f)]
    public async Task OpenAIRejectsNonDefaultSpeedsBeforeBufferedOrStreamingRequests(float speed)
    {
        var provider = Create(NoNetwork);
        var request = new AudioSpeechRequest
        {
            Model = "wellsaid/3", Input = "Hello", Voice = "alloy", Speed = speed, StreamFormat = "sse"
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(request));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(request)) { }
        });
    }

    [Fact]
    public async Task OpenAIExtensionsAndProviderEnvelopePassThrough()
    {
        var provider = Create(async (message, token) =>
        {
            var body = await Body(message, token);
            Assert.Equal("caruso", body.GetProperty("model").GetString());
            Assert.Equal("native", body.GetProperty("future_option").GetString());
            Assert.Equal(7, body.GetProperty("nested").GetProperty("value").GetInt32());
            Assert.False(body.TryGetProperty("providerOptions", out _));
            Assert.False(body.TryGetProperty("unrelated", out _));
            return Audio([1]);
        });
        await provider.OpenAISpeechRequestAsync(new()
        {
            Model = "wellsaid/3", Input = "Hello",
            AdditionalProperties = new()
            {
                ["model"] = Element("overridden"),
                ["nested"] = Element(new { value = 7 }),
                ["providerOptions"] = Element(new
                {
                    wellsaid = new { model = "caruso", future_option = "native" },
                    other = new { unrelated = true }
                })
            }
        });
    }

    [Fact]
    public async Task StreamingForwardsAudioIncrementallyAndDoesNotInventUsage()
    {
        var bytes = Enumerable.Range(0, 9000).Select(value => (byte)value).ToArray();
        var provider = Create((_, _) => Task.FromResult(Audio(bytes)));
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in provider.OpenAISpeechStreamingAsync(StreamRequest()))
            events.Add(item);
        Assert.Equal(3, events.Count);
        var audio = events.OfType<AudioSpeechStreamDelta>().SelectMany(delta => Convert.FromBase64String(delta.Audio)).ToArray();
        Assert.Equal(bytes, audio);
        Assert.Null(Assert.IsType<AudioSpeechStreamDone>(events[^1]).Usage);
    }

    [Fact]
    public async Task CancellationAfterFirstDeltaDoesNotEmitDone()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Create((_, _) => Task.FromResult(Audio(new byte[9000])));
        await using var enumerator = provider.OpenAISpeechStreamingAsync(StreamRequest(), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<AudioSpeechStreamDelta>(enumerator.Current);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeHttpErrorsPreserveStatusAndBody(bool discovery)
    {
        var provider = Create((_, _) => Task.FromResult(Json("""{"message":"denied"}""", HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (discovery)
                await provider.ListModels();
            else
                await provider.SpeechRequest(Request());
        });
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("denied", error.Message);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(StreamRequest())) { }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyOrNonAudioSuccessDoesNotSucceed(bool empty)
    {
        var provider = Create((_, _) => Task.FromResult(empty ? Audio([]) : Json("""{"error":"failure"}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(StreamRequest()))
                Assert.IsNotType<AudioSpeechStreamDone>(item);
        });
    }

    [Fact]
    public async Task InvalidRequestsAreRejectedBeforeNetwork()
    {
        var provider = Create(NoNetwork);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new() { Model = "wellsaid/../3", Text = "Hi" }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new() { Model = "wellsaid/3", Text = " " }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new() { Model = "3", Text = "Hi", OutputFormat = "wav" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new() { Model = "3", Text = "Hi", Speed = 1.2f }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new() { Model = "3", Text = "Hi", Instructions = "whisper" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new() { Model = "3", Text = "Hi", Language = "en" }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new()
        {
            Model = "3", Text = "Hi", ProviderOptions = new() { ["wellsaid"] = Element(1) }
        }));
        foreach (var native in new[] { Element(1), Element(new { wellsaid = 1 }) })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAISpeechRequestAsync(new()
            {
                Model = "3", Input = "Hi", AdditionalProperties = new() { ["providerOptions"] = native }
            }));
        var stream = StreamRequest();
        stream.StreamFormat = "audio";
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(stream)) { }
        });
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAndDuringExecution()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var provider = Create(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SpeechRequest(Request(), cancelled.Token));
        provider = Create(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Audio([1]);
        });
        using var inFlight = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SpeechRequest(Request(), inFlight.Token));
    }

    [Fact]
    public async Task ScopedRegistrationAndHeaderAuthExecuteEveryDiscoveredAvatar()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-WellSaid-Key"] = "  test-key  ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("WELLSAID"));
        Assert.Equal("X-WellSaid-Key", HeaderApiKeyResolver.SupportedProviderHeaders["wellsaid"]);
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            descriptor => descriptor.ImplementationType == typeof(WellSaidProvider)).Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(async (message, token) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
                return Json(Avatars);
            var body = await Body(message, token);
            Assert.Contains(body.GetProperty("speaker_id").GetInt32(), new[] { 3, 12 });
            return Audio([1]);
        })));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("wellsaid"));
        var provider = Assert.IsType<WellSaidProvider>(registry.GetModelProvider(scope.ServiceProvider, "wellsaid"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<WellSaidProvider>(), provider);
        foreach (var model in await provider.ListModels())
            await provider.SpeechRequest(new() { Model = model.Id, Text = "Hello" });
    }

    private static SpeechRequest Request() => new() { Model = "wellsaid/3", Text = "Hello" };
    private static AudioSpeechRequest StreamRequest() => new() { Model = "wellsaid/3", Input = "Hello", StreamFormat = "sse" };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage message, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("X-Api-Key")));
        Assert.Null(message.Headers.Authorization);
        Assert.Empty(message.RequestUri!.Query);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static WellSaidProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(responder)));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Audio(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("audio/mpeg");
        return response;
    }
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "wellsaid" ? key : null;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
