using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Neuphonic;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Neuphonic;

public sealed class NeuphonicProviderTests
{
    private const string Voices = """
        {"data":{"voices":[
          {"id":"database-id","voice_id":"voice-one","name":"One","lang_code":"en","tags":["warm"],"model_availability":["native-model"],"type":"standard"},
          {"voice_id":"voice-two","name":"Two","lang_code":"es","type":"cloned"},
          {"voice_id":"voice-one","name":"Duplicate","lang_code":"en"}
        ]}}
        """;
    private const string Speech = """
        : keepalive

        event: message
        data: {"status_code":200,"data":{"audio":"AQI=","text":"Hello","sampling_rate":24000},"metadata":{"native":"value"}}

        event: message
        data: {"status_code":200,
        data: "data":{"audio":"AwQ=","text":" world","sampling_rate":24000}}


        """;

    [Fact]
    public async Task DiscoveryPreservesNativeVoiceAndLanguage()
    {
        var provider = Create((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal("https://api.neuphonic.com/voices", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            return Task.FromResult(Json(Voices));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["neuphonic/en/voice-one", "neuphonic/es/voice-two"], models.Select(model => model.Id));
        Assert.Equal(["One", "Two"], models.Select(model => model.Name));
        Assert.All(models, model =>
        {
            Assert.Equal("speech", model.Type);
            Assert.Equal("Neuphonic", model.OwnedBy);
            Assert.Null(model.Pricing);
            Assert.Null(model.ContextWindow);
        });
    }

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

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"voices\":{}}}")]
    [InlineData("{\"data\":{\"voices\":[null]}}")]
    [InlineData("{\"data\":{\"voices\":[{\"id\":\"not-a-voice-id\",\"lang_code\":\"en\"}]}}")]
    [InlineData("{\"data\":{\"voices\":[{\"voice_id\":\"one\"}]}}")]
    public async Task MalformedDiscoveryDoesNotInventVoicesOrLanguages(string body)
        => await Assert.ThrowsAsync<JsonException>(() => Create((_, _) => Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task EmptyVoiceLibraryRemainsEmpty()
        => Assert.Empty(await Create((_, _) => Task.FromResult(Json("""{"data":{"voices":[]}}"""))).ListModels());

    [Fact]
    public async Task SpeechMapsStandardFieldsAndPreservesNativePassthroughAndMetadata()
    {
        var request = Request();
        request.Language = "fr";
        request.Voice = "explicit-voice";
        request.Speed = 1.2f;
        request.OutputFormat = "pcm";
        request.ProviderOptions = new()
        {
            ["neuphonic"] = Element(new
            {
                text = "wrong", voice_id = "wrong", lang_code = "es", speed = 0.5,
                sampling_rate = 16000, temperature = 0.7,
                encoding = "pcm_linear", future_option = new { enabled = true }
            }),
            ["other"] = Element(new { unrelated = true })
        };
        var original = request.ProviderOptions["neuphonic"].GetRawText();
        var provider = Create(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.neuphonic.com/sse/speak/fr", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType);
            var body = await Body(message, token);
            Assert.Equal("Hello", body.GetProperty("text").GetString());
            Assert.Equal("explicit-voice", body.GetProperty("voice_id").GetString());
            Assert.Equal("fr", body.GetProperty("lang_code").GetString());
            Assert.Equal(1.2f, body.GetProperty("speed").GetSingle());
            Assert.False(body.TryGetProperty("output_format", out _));
            Assert.Equal(16000, body.GetProperty("sampling_rate").GetInt32());
            Assert.Equal(0.7, body.GetProperty("temperature").GetDouble());
            Assert.Equal("pcm_linear", body.GetProperty("encoding").GetString());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("unrelated", out _));
            Assert.False(body.TryGetProperty("stream", out _));
            return Sse(Speech);
        });
        var response = await provider.SpeechRequest(request);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal("audio/pcm", response.Audio.MimeType);
        Assert.Equal("pcm", response.Audio.Format);
        Assert.Equal("neuphonic/fr/explicit-voice", response.Response.ModelId);
        Assert.Empty(response.Warnings);
        var events = response.ProviderMetadata!["neuphonic"].GetProperty("events");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal(24000, events[0].GetProperty("data").GetProperty("sampling_rate").GetInt32());
        Assert.Equal("value", events[0].GetProperty("metadata").GetProperty("native").GetString());
        Assert.False(events[0].GetProperty("data").TryGetProperty("audio", out _));
        Assert.Equal(original, request.ProviderOptions["neuphonic"].GetRawText());
    }

    [Fact]
    public async Task OpenAIAdditionalPropertiesPassThroughWithoutSynthesisDefaults()
    {
        var provider = Create(async (message, token) =>
        {
            var body = await Body(message, token);
            Assert.Equal("voice-one", body.GetProperty("voice_id").GetString());
            Assert.Equal("en", body.GetProperty("lang_code").GetString());
            Assert.Equal(0.7, body.GetProperty("temperature").GetDouble());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            foreach (var field in new[] { "encoding", "sampling_rate", "output_format", "speed", "model", "stream" })
                Assert.False(body.TryGetProperty(field, out _));
            return Sse(Speech);
        });
        var response = await provider.OpenAISpeechRequestAsync(new()
        {
            Model = "neuphonic/en/voice-one", Input = "Hello",
            AdditionalProperties = new()
            {
                ["temperature"] = Element(0.7),
                ["future_option"] = Element(new { enabled = true })
            }
        });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, response.Audio);
        Assert.Equal("audio/pcm", response.MimeType);
    }

    [Theory]
    [InlineData(null, "audio/pcm", "pcm")]
    [InlineData("pcm_linear", "audio/pcm", "pcm")]
    [InlineData("pcm_mulaw", "application/octet-stream", "pcm_mulaw")]
    public async Task RawAudioIsLabeledByItsNativeEncoding(string? encoding, string mime, string expectedFormat)
    {
        var request = Request();
        request.ProviderOptions = new() { ["neuphonic"] = Element(new { output_format = (string?)null, encoding }) };
        var result = await Create(async (message, token) =>
        {
            var body = await Body(message, token);
            Assert.False(body.TryGetProperty("output_format", out _));
            if (encoding is null)
                Assert.False(body.TryGetProperty("encoding", out _));
            else
                Assert.Equal(encoding, body.GetProperty("encoding").GetString());
            return Sse(Speech);
        }).SpeechRequest(request);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Convert.FromBase64String(result.Audio.Base64));
        Assert.Equal(mime, result.Audio.MimeType);
        Assert.Equal(expectedFormat, result.Audio.Format);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("wav")]
    [InlineData("opus")]
    [InlineData("future-format")]
    public async Task UnsupportedFormatsFailBeforeNetworkOnAllSpeechSurfaces(string format)
    {
        var provider = Create(NoNetwork);
        var request = Request();
        request.OutputFormat = format;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        var openAI = StreamRequest();
        openAI.ResponseFormat = format;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(openAI));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(openAI)) { }
        });
    }

    [Theory]
    [InlineData("output_format", "\"mp3\"")]
    [InlineData("output_format", "\"wav\"")]
    [InlineData("output_format", "\"pcm\"")]
    [InlineData("output_format", "123")]
    [InlineData("encoding", "\"mp3\"")]
    [InlineData("encoding", "\"future-encoding\"")]
    [InlineData("encoding", "123")]
    public async Task UnsupportedNativeFormatsAndEncodingsFailBeforeNetwork(string field, string value)
    {
        var provider = Create(NoNetwork);
        var native = new Dictionary<string, JsonElement> { [field] = JsonSerializer.Deserialize<JsonElement>(value) };
        var request = Request();
        request.ProviderOptions = new() { ["neuphonic"] = Element(native) };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        var openAI = StreamRequest();
        openAI.AdditionalProperties = native;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(openAI));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(openAI)) { }
        });
    }

    [Fact]
    public async Task PCMCompatibilityMapsOnlyNecessaryEncodingAndNativeLanguageSelectsRoute()
    {
        var request = Request();
        request.Model = "NEUPHONIC/en/voice-one";
        request.OutputFormat = "pcm";
        request.ProviderOptions = new() { ["neuphonic"] = Element(new { lang_code = "es", encoding = "pcm_mulaw" }) };
        var provider = Create(async (message, token) =>
        {
            Assert.Equal("/sse/speak/es", message.RequestUri!.AbsolutePath);
            var body = await Body(message, token);
            Assert.Equal("pcm_linear", body.GetProperty("encoding").GetString());
            Assert.False(body.TryGetProperty("output_format", out _));
            return Sse(Speech);
        });
        Assert.Equal("audio/pcm", (await provider.SpeechRequest(request)).Audio.MimeType);
    }

    [Fact]
    public async Task StreamingEmitsNativeAudioChunksThenDoneWithoutInventedUsage()
    {
        var provider = Create((_, _) => Task.FromResult(Sse(Speech)));
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in provider.OpenAISpeechStreamingAsync(StreamRequest()))
            events.Add(item);
        Assert.Equal(3, events.Count);
        Assert.Equal("AQI=", Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.Equal("AwQ=", Assert.IsType<AudioSpeechStreamDelta>(events[1]).Audio);
        Assert.Null(Assert.IsType<AudioSpeechStreamDone>(events[2]).Usage);
    }

    [Fact]
    public async Task StreamingHandlesMetadataOnlyEvents()
    {
        const string body = "data: {\"status_code\":200,\"metadata\":{\"native\":true}}\n\n" +
                            "data: {\"status_code\":200,\"data\":{\"audio\":\"AQI=\"}}\n\n";
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in Create((_, _) => Task.FromResult(Sse(body))).OpenAISpeechStreamingAsync(StreamRequest()))
            events.Add(item);
        Assert.Equal("AQI=", Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[1]);
    }

    [Theory]
    [InlineData("data: {\"status_code\":429,\"errors\":[\"limit\"]}\n\n")]
    [InlineData("data: {\"status_code\":200,\"errors\":[\"failure\"]}\n\n")]
    [InlineData("event: error\ndata: {\"errors\":null}\n\n")]
    public async Task InBandErrorsFailBothSpeechSurfacesWithoutDone(string body)
    {
        var provider = Create((_, _) => Task.FromResult(Sse(body)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(Request()));
        Assert.Contains("errors", error.Message);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(StreamRequest()))
                Assert.IsNotType<AudioSpeechStreamDone>(item);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("data: {\"status_code\":200,\"data\":{\"audio\":null}}\n\n")]
    public async Task EmptyAudioIsNotReportedAsSuccess(string body)
    {
        var provider = Create((_, _) => Task.FromResult(Sse(body)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(StreamRequest()))
                Assert.IsNotType<AudioSpeechStreamDone>(item);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeHttpErrorsPreserveStatusAndBody(bool discovery)
    {
        var provider = Create((_, _) => Task.FromResult(Json("""{"detail":"denied"}""", HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (discovery)
                await provider.ListModels();
            else
                await provider.SpeechRequest(Request());
        });
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("denied", error.Message);
    }

    [Fact]
    public async Task ValidationRejectsUnsupportedOrIncompleteRequestsBeforeNetwork()
    {
        var provider = Create(NoNetwork);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new() { Model = "voice-only", Text = "Hello" }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new() { Model = "en/", Text = "Hello" }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new() { Model = "en/voice", Text = " " }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new() { Model = "en/voice", Text = "Hi", Instructions = "whisper" }));
        var request = Request();
        request.ProviderOptions = new() { ["neuphonic"] = Element(1) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(request));
        var streamRequest = StreamRequest();
        streamRequest.StreamFormat = "audio";
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(streamRequest)) { }
        });
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAndDuringExecution()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Create(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SpeechRequest(Request(), cancellation.Token));
        provider = Create(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Sse(Speech);
        });
        using var inFlight = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SpeechRequest(Request(), inFlight.Token));
    }

    [Fact]
    public async Task CancellationAfterFirstDeltaDoesNotEmitDone()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Create((_, _) => Task.FromResult(Sse(Speech)));
        await using var enumerator = provider.OpenAISpeechStreamingAsync(StreamRequest(), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<AudioSpeechStreamDelta>(enumerator.Current);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task HeaderScopedRegistrationResolvesAndEveryDiscoveredEntryCanExecute()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Neuphonic-Key"] = "  test-key  ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("NEUPHONIC"));
        Assert.Equal("X-Neuphonic-Key", HeaderApiKeyResolver.SupportedProviderHeaders["neuphonic"]);
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            item => item.ImplementationType == typeof(NeuphonicProvider)).Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(async (message, token) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
                return Json(Voices);
            var body = await Body(message, token);
            Assert.Equal($"/sse/speak/{body.GetProperty("lang_code").GetString()}", message.RequestUri!.AbsolutePath);
            Assert.Contains(body.GetProperty("voice_id").GetString(), new[] { "voice-one", "voice-two" });
            return Sse(Speech);
        })));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("neuphonic"));
        var provider = Assert.IsType<NeuphonicProvider>(registry.GetModelProvider(scope.ServiceProvider, "neuphonic"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<NeuphonicProvider>(), provider);
        foreach (var model in await provider.ListModels())
            await provider.SpeechRequest(new() { Model = model.Id, Text = "Hello" });
    }

    private static SpeechRequest Request() => new() { Model = "neuphonic/en/voice-one", Text = "Hello" };
    private static AudioSpeechRequest StreamRequest() => new() { Model = "neuphonic/en/voice-one", Input = "Hello", StreamFormat = "sse" };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage message, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("x-api-key")));
        Assert.Null(message.Headers.Authorization);
        Assert.Empty(message.RequestUri!.Query);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static NeuphonicProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(responder)));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "neuphonic" ? key : null;
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
