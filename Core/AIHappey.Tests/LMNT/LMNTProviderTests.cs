using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.LMNT;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.LMNT;

public sealed class LMNTProviderTests
{
    private const string Voices = """
        [
          {"id":"voice/one","name":"One","state":"ready","owner":"system","description":"Description","tags":["warm"],"preview_url":"https://example.com/preview"},
          {"id":"pending","name":"Pending","state":"training","owner":"me"},
          {"id":"voice/one","name":"Duplicate","state":"ready","owner":"system"},
          {"id":"two","name":"Two","state":"ready","owner":"me"}
        ]
        """;

    [Fact]
    public async Task DiscoveryUsesAuthenticatedNativeVoicesAndPreservesMetadata()
    {
        var provider = CreateProvider((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal("https://api.lmnt.com/v1/ai/voice/list", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            return Task.FromResult(JsonResponse(Voices));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["lmnt/voice/one", "lmnt/two"], models.Select(model => model.Id));
        Assert.Equal(["One", "Two"], models.Select(model => model.Name));
        Assert.All(models, model =>
        {
            Assert.Equal("speech", model.Type);
            Assert.Equal("LMNT", model.OwnedBy);
            Assert.Null(model.Pricing);
            Assert.Null(model.ContextWindow);
        });
        Assert.Equal("Description", models[0].Description);
        Assert.Equal("voice/one", models[0].Metadata!["lmnt"].GetProperty("id").GetString());
        Assert.Equal("warm", models[0].Metadata!["lmnt"].GetProperty("tags")[0].GetString());
        Assert.Equal("me", models[1].Metadata!["lmnt"].GetProperty("owner").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeySkipsDiscoveryAndRejectsExecution(string? key)
    {
        var provider = CreateProvider(NoNetwork, key);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":\"x\"}]")]
    [InlineData("[{\"id\":1,\"state\":\"ready\"}]")]
    public async Task InvalidDiscoveryDoesNotInventModels(string json)
        => await Assert.ThrowsAsync<JsonException>(() =>
            CreateProvider((_, _) => Task.FromResult(JsonResponse(json))).ListModels());

    [Fact]
    public async Task VercelSpeechMapsFieldsPreservesNativeOptionsAndResponseHeaders()
    {
        var request = Request();
        request.OutputFormat = "wav";
        request.Language = "en";
        request.ProviderOptions = new()
        {
            ["lmnt"] = Element(new
            {
                text = "wrong", voice = "wrong", format = "mp3", language = "fr",
                model = "blizzard", sample_rate = 16000, temperature = 0.8,
                future_option = new { enabled = true }
            }),
            ["other"] = Element(new { unrelated = true })
        };
        var original = request.ProviderOptions["lmnt"].GetRawText();
        var provider = CreateProvider(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.lmnt.com/v1/ai/speech/bytes", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            Assert.Equal("application/octet-stream", Assert.Single(message.Headers.Accept).MediaType);
            var body = await ReadBody(message, token);
            Assert.Equal("Hello", body.GetProperty("text").GetString());
            Assert.Equal("voice/one", body.GetProperty("voice").GetString());
            Assert.Equal("wav", body.GetProperty("format").GetString());
            Assert.Equal("en", body.GetProperty("language").GetString());
            Assert.Equal("blizzard", body.GetProperty("model").GetString());
            Assert.Equal(16000, body.GetProperty("sample_rate").GetInt32());
            Assert.Equal(0.8, body.GetProperty("temperature").GetDouble());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("unrelated", out _));
            return AudioResponse([1, 2, 3]);
        });
        var response = await provider.SpeechRequest(request);
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Equal("lmnt/voice/one", response.Response.ModelId);
        Assert.Equal("request-1", response.Response.Headers!["x-request-id"]);
        Assert.Equal(3, response.ProviderMetadata!["lmnt"].GetProperty("bytes").GetInt32());
        Assert.Empty(response.Warnings);
        Assert.Equal(original, request.ProviderOptions["lmnt"].GetRawText());
    }

    [Fact]
    public async Task OpenAISpeechForwardsAdditionalPropertiesWithoutInjectingDefaults()
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal("native-model", body.GetProperty("model").GetString());
            Assert.Equal("voice/one", body.GetProperty("voice").GetString());
            Assert.Equal(0.4, body.GetProperty("top_p").GetDouble());
            Assert.False(body.TryGetProperty("format", out _));
            Assert.False(body.TryGetProperty("language", out _));
            Assert.False(body.TryGetProperty("sample_rate", out _));
            Assert.False(body.TryGetProperty("debug", out _));
            return AudioResponse([1, 2], "audio/mpeg");
        });
        var response = await provider.OpenAISpeechRequestAsync(new()
        {
            Model = "lmnt/voice/one", Input = "Hello",
            AdditionalProperties = new()
            {
                ["model"] = Element("native-model"), ["top_p"] = Element(0.4)
            }
        });
        Assert.Equal(new byte[] { 1, 2 }, response.Audio);
        Assert.Equal("audio/mpeg", response.MimeType);
    }

    [Theory]
    [InlineData("LMNT/voice/one")]
    [InlineData("voice/one")]
    public async Task ExplicitVoiceAndPCMCompatibilityAreMapped(string model)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal("two", body.GetProperty("voice").GetString());
            Assert.Equal("pcm_s16le", body.GetProperty("format").GetString());
            return AudioResponse([1, 2]);
        });
        await provider.OpenAISpeechRequestAsync(new()
        {
            Model = model, Input = "Hello", Voice = "two", ResponseFormat = "pcm"
        });
    }

    [Fact]
    public async Task NativeFormatIsRetainedWhenStandardFormatIsAbsent()
    {
        var request = Request();
        request.ProviderOptions = new() { ["lmnt"] = Element(new { format = "webm" }) };
        var response = await CreateProvider(async (message, token) =>
        {
            Assert.Equal("webm", (await ReadBody(message, token)).GetProperty("format").GetString());
            return AudioResponse([1, 2]);
        }).SpeechRequest(request);
        Assert.Equal("webm", response.Audio.Format);
        Assert.Equal("audio/webm", response.Audio.MimeType);
    }

    [Fact]
    public async Task StreamingYieldsNativeBinaryChunksAndDoneWithoutInventedUsage()
    {
        var audio = Enumerable.Range(0, 20000).Select(index => (byte)index).ToArray();
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal("voice/one", body.GetProperty("voice").GetString());
            Assert.Equal(16000, body.GetProperty("sample_rate").GetInt32());
            return AudioResponse(audio);
        });
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var part in provider.OpenAISpeechStreamingAsync(new()
        {
            Model = "lmnt/voice/one", Input = "Hello", StreamFormat = "sse",
            AdditionalProperties = new() { ["sample_rate"] = Element(16000) }
        }))
            events.Add(part);
        Assert.True(events.OfType<AudioSpeechStreamDelta>().Count() > 1);
        Assert.Equal(audio, events.OfType<AudioSpeechStreamDelta>()
            .SelectMany(part => Convert.FromBase64String(part.Audio)).ToArray());
        Assert.Null(Assert.IsType<AudioSpeechStreamDone>(events[^1]).Usage);
        Assert.Single(events.OfType<AudioSpeechStreamDone>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NativeErrorsPreserveStatusAndBodyAcrossDiscoveryAndSpeech(HttpStatusCode status)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""{"error":"native detail"}""", status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("native detail", error.Message);
        error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(Request()));
        Assert.Equal(status, error.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(StreamRequest())) { }
        });
    }

    [Fact]
    public async Task EmptyAudioAndUnsupportedFieldsFailExplicitly()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(AudioResponse([])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(StreamRequest())) { }
        });
        provider = CreateProvider(NoNetwork);
        var request = Request();
        request.Speed = 1.2f;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        request.Speed = null;
        request.Instructions = "whisper";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.SpeechRequest(null!));
        foreach (var model in new[] { "", " ", "lmnt/" })
        {
            request = Request();
            request.Model = model;
            await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(request));
        }
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeRequestsAndBetweenStreamChunks()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SpeechRequest(Request(), cts.Token));
        using var streaming = new CancellationTokenSource();
        provider = CreateProvider((_, _) => Task.FromResult(AudioResponse(new byte[20000])));
        await using var enumerator = provider.OpenAISpeechStreamingAsync(StreamRequest(), streaming.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<AudioSpeechStreamDelta>(enumerator.Current);
        streaming.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task HeaderScopedRegistryAndDiscoveredModelsAreExecutable()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-LMNT-Key"] = "  test-key  ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("LMNT"));
        Assert.Equal("X-LMNT-Key", HeaderApiKeyResolver.SupportedProviderHeaders["lmnt"]);
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            item => item.ImplementationType == typeof(LMNTProvider)).Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(async (message, token) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
                return JsonResponse(Voices);
            Assert.Contains((await ReadBody(message, token)).GetProperty("voice").GetString(), new[] { "voice/one", "two" });
            return AudioResponse([1, 2]);
        })));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("lmnt"));
        var provider = Assert.IsType<LMNTProvider>(registry.GetModelProvider(scope.ServiceProvider, "lmnt"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<LMNTProvider>(), provider);
        foreach (var model in await provider.ListModels())
        {
            var request = Request();
            request.Model = model.Id;
            await provider.SpeechRequest(request);
        }
    }

    private static SpeechRequest Request() => new() { Model = "lmnt/voice/one", Text = "Hello" };
    private static AudioSpeechRequest StreamRequest() => new() { Model = "lmnt/voice/one", Input = "Hello", StreamFormat = "sse" };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> ReadBody(HttpRequestMessage message, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("X-API-Key")));
        Assert.Equal("1.2", Assert.Single(message.Headers.GetValues("lmnt-version")));
        Assert.Null(message.Headers.Authorization);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static LMNTProvider CreateProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(responder)));
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage AudioResponse(byte[] audio, string contentType = "application/octet-stream")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
        response.Content.Headers.ContentType = new(contentType);
        response.Headers.Add("x-request-id", "request-1");
        return response;
    }
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "lmnt" ? key : null;
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
