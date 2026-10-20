using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Respeecher;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Respeecher;

public sealed class RespeecherProviderTests
{
    private static readonly string[] Models = ["en-rt", "ua-rt", "ar-rt", "de-rt", "pt-br-rt"];
    private const string Voices = """
        [{"id":"voice/one","full_name":"One","gender":"female","accent":"native","age":30,
          "sampling_params":{"temperature":0.6,"future_parameter":true},"future_metadata":{"enabled":true}},
         {"id":"voice/one","full_name":"Duplicate"}]
        """;
    private const string Chunks = """
        {"type":"chunk","data":"AQI="}

        {"type":"chunk","data":"AwQ="}
        """;

    [Fact]
    public async Task DiscoveryUsesFiveDocumentedModelPathsAndPreservesUsableVoicesAndMetadata()
    {
        var paths = new List<string>();
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
            {
                paths.Add(message.RequestUri!.AbsolutePath);
                return Json(Voices);
            }
            var body = await Body(message, token);
            Assert.Equal("voice/one", body.GetProperty("voice").GetProperty("id").GetString());
            Assert.Contains(message.RequestUri!.AbsolutePath,
                Models.Select(model => $"/v1/public/tts/{model}/tts/bytes"));
            return Wav();
        });
        var discovered = (await provider.ListModels()).ToArray();
        Assert.Equal(Models.Select(model => $"/v1/public/tts/{model}/voices"), paths);
        Assert.Equal(Models.Select(model => $"respeecher/{model}/voice/one"), discovered.Select(model => model.Id));
        foreach (var model in discovered)
        {
            Assert.Equal("One", model.Name);
            Assert.Equal("speech", model.Type);
            Assert.Equal("Respeecher", model.OwnedBy);
            Assert.Null(model.Pricing);
            Assert.Null(model.ContextWindow);
            var metadata = model.Metadata!["respeecher"];
            Assert.Equal("female", metadata.GetProperty("gender").GetString());
            Assert.Equal("native", metadata.GetProperty("accent").GetString());
            Assert.Equal(30, metadata.GetProperty("age").GetInt32());
            Assert.True(metadata.GetProperty("sampling_params").GetProperty("future_parameter").GetBoolean());
            Assert.True(metadata.GetProperty("future_metadata").GetProperty("enabled").GetBoolean());
            var result = await provider.SpeechRequest(new() { Model = model.Id, Text = "Hello" });
            Assert.Equal(model.Id, result.Response.ModelId);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeyReturnsNoModelsAndRejectsAllSpeech(string? key)
    {
        var provider = Create(NoNetwork, key);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAISpeechRequestAsync(OpenAI()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(provider.OpenAISpeechStreamingAsync(OpenAI())));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":123}]")]
    [InlineData("[{\"id\":\" \"}]")]
    [InlineData("not json")]
    public async Task MalformedDiscoveryFailsWithoutFallback(string body)
        => await Assert.ThrowsAnyAsync<JsonException>(() => Create((_, _) => Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task EmptyDiscoveryRemainsEmptyAcrossAllLanguages()
    {
        var requests = 0;
        Assert.Empty(await Create((_, _) =>
        {
            requests++;
            return Task.FromResult(Json("[]"));
        }).ListModels());
        Assert.Equal(5, requests);
    }

    [Fact]
    public async Task StandardSpeechMapsTextAndVoiceWithoutMutatingNativeOptions()
    {
        var request = Request();
        request.Voice = "explicit/voice";
        request.OutputFormat = "wav";
        request.ProviderOptions = new()
        {
            ["respeecher"] = Element(new
            {
                transcript = "wrong",
                voice = new { id = "wrong", sampling_params = new { temperature = 0.6, future_sampling = new[] { 1, 2 } }, future_voice = true },
                output_format = new { sample_rate = 16000, future_output = true },
                future_option = new { enabled = true }
            }),
            ["other"] = Element(new { unrelated = true })
        };
        var original = request.ProviderOptions["respeecher"].GetRawText();
        var provider = Create(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.respeecher.com/v1/public/tts/en-rt/tts/bytes", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            var body = await Body(message, token);
            Assert.Equal("Hello", body.GetProperty("transcript").GetString());
            var voice = body.GetProperty("voice");
            Assert.Equal("explicit/voice", voice.GetProperty("id").GetString());
            Assert.Equal(0.6, voice.GetProperty("sampling_params").GetProperty("temperature").GetDouble());
            Assert.Equal(2, voice.GetProperty("sampling_params").GetProperty("future_sampling").GetArrayLength());
            Assert.True(voice.GetProperty("future_voice").GetBoolean());
            Assert.Equal(16000, body.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            Assert.True(body.GetProperty("output_format").GetProperty("future_output").GetBoolean());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            foreach (var field in new[] { "model", "stream", "unrelated", "speed", "instructions" })
                Assert.False(body.TryGetProperty(field, out _));
            return Wav();
        });
        var response = await provider.SpeechRequest(request);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Equal(Encoding.ASCII.GetBytes("RIFFtestWAVE"), Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal("respeecher/en-rt/explicit/voice", response.Response.ModelId);
        Assert.Empty(response.Warnings);
        Assert.Null(response.ProviderMetadata);
        Assert.Equal(original, request.ProviderOptions["respeecher"].GetRawText());
    }

    [Fact]
    public async Task OpenAIAdditionalPropertiesPassThroughOnBothSurfacesWithoutDefaultsOrMutation()
    {
        var options = OpenAI();
        options.AdditionalProperties = new()
        {
            ["transcript"] = Element("wrong"),
            ["voice"] = Element(new { id = "wrong", sampling_params = new { future_sampling = true } }),
            ["output_format"] = Element(new { sample_rate = 24000 }),
            ["future_option"] = Element(new[] { "a", "b" })
        };
        var original = Element(options.AdditionalProperties).GetRawText();
        var provider = Create(async (message, token) =>
        {
            var body = await Body(message, token);
            Assert.Equal("Hello", body.GetProperty("transcript").GetString());
            Assert.Equal("voice-one", body.GetProperty("voice").GetProperty("id").GetString());
            Assert.True(body.GetProperty("voice").GetProperty("sampling_params").GetProperty("future_sampling").GetBoolean());
            Assert.Equal(24000, body.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            Assert.False(body.GetProperty("output_format").TryGetProperty("encoding", out _));
            Assert.Equal(2, body.GetProperty("future_option").GetArrayLength());
            Assert.Equal(4, body.EnumerateObject().Count());
            return message.RequestUri!.AbsolutePath.EndsWith("/bytes") ? Wav() : Json(Chunks);
        });
        var audio = await provider.OpenAISpeechRequestAsync(options);
        Assert.Equal("audio/wav", audio.MimeType);
        Assert.Equal(Encoding.ASCII.GetBytes("RIFFtestWAVE"), audio.Audio);
        Assert.Equal(3, (await Drain(provider.OpenAISpeechStreamingAsync(options))).Count);
        Assert.Equal(original, Element(options.AdditionalProperties).GetRawText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pcm_f32le")]
    [InlineData("pcm_s16le")]
    [InlineData("pcm_mulaw")]
    public async Task StreamingReadsFragmentedJsonLinesAndEmitsDeltasAndDoneWithoutUsage(string? encoding)
    {
        var options = OpenAI();
        if (encoding is not null)
            options.AdditionalProperties = new() { ["output_format"] = Element(new { encoding, sample_rate = 8000 }) };
        var provider = Create(async (message, token) =>
        {
            Assert.Equal("https://api.respeecher.com/v1/public/tts/en-rt/tts/sse", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            var body = await Body(message, token);
            if (encoding is null)
                Assert.False(body.TryGetProperty("output_format", out _));
            else
            {
                Assert.Equal(encoding, body.GetProperty("output_format").GetProperty("encoding").GetString());
                Assert.Equal(8000, body.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            }
            return new(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(Chunks))) };
        });
        var events = await Drain(provider.OpenAISpeechStreamingAsync(options));
        Assert.Equal(3, events.Count);
        Assert.Equal("AQI=", Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.Equal("AwQ=", Assert.IsType<AudioSpeechStreamDelta>(events[1]).Audio);
        Assert.Null(Assert.IsType<AudioSpeechStreamDone>(events[2]).Usage);
    }

    [Fact]
    public async Task StandardPcmTranslationChangesOnlyNativeEncoding()
    {
        var options = OpenAI();
        options.ResponseFormat = "pcm";
        options.AdditionalProperties = new() { ["output_format"] = Element(new { encoding = "pcm_mulaw", sample_rate = 16000, future = true }) };
        var original = Element(options.AdditionalProperties).GetRawText();
        var provider = Create(async (message, token) =>
        {
            var format = (await Body(message, token)).GetProperty("output_format");
            Assert.Equal("pcm_s16le", format.GetProperty("encoding").GetString());
            Assert.Equal(16000, format.GetProperty("sample_rate").GetInt32());
            Assert.True(format.GetProperty("future").GetBoolean());
            return Json(Chunks);
        });
        await Drain(provider.OpenAISpeechStreamingAsync(options));
        Assert.Equal(original, Element(options.AdditionalProperties).GetRawText());
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("opus")]
    [InlineData("future")]
    public async Task UnsupportedStandardFormatsFailWithoutNetwork(string format)
    {
        var provider = Create(NoNetwork);
        var request = Request();
        request.OutputFormat = format;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        var options = OpenAI();
        options.ResponseFormat = format;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
    }

    [Fact]
    public async Task WavStreamingAndPcmBytesAreRejected()
    {
        var provider = Create(NoNetwork);
        var options = OpenAI();
        options.ResponseFormat = "wav";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
        options.ResponseFormat = "pcm";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
    }

    [Fact]
    public async Task UnsupportedSpeedInstructionsLanguageAndStreamFormatAreNotSilentlyIgnored()
    {
        var provider = Create(NoNetwork);
        var request = Request();
        request.Speed = 1;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        request.Speed = null;
        request.Instructions = "Speak softly";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        request.Instructions = null;
        request.Language = "de";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        var options = OpenAI();
        options.Speed = 1;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
        options.Speed = null;
        options.Instructions = "Softly";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
        options.Instructions = null;
        options.StreamFormat = "audio";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
    }

    [Theory]
    [InlineData("respeecher/../voice")]
    [InlineData("respeecher/./voice")]
    [InlineData("respeecher/%2e%2e/voice")]
    [InlineData("respeecher/en-rt?x=1/voice")]
    [InlineData("respeecher/en-rt#fragment/voice")]
    [InlineData("respeecher/en\\rt/voice")]
    [InlineData("respeecher//voice")]
    [InlineData("respeecher/en-rt/")]
    [InlineData("en-rt")]
    public async Task UnsafeOrIncompleteModelIdsNeverReachNetwork(string model)
    {
        var request = Request();
        request.Model = model;
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoNetwork).SpeechRequest(request));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"wrong\"")]
    public async Task MalformedNativeOptionsAreRejected(string native)
    {
        var request = Request();
        request.ProviderOptions = new() { ["respeecher"] = JsonSerializer.Deserialize<JsonElement>(native) };
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoNetwork).SpeechRequest(request));
    }

    [Theory]
    [InlineData("voice", "123")]
    public async Task MalformedNativeNestedObjectsAreRejected(string field, string json)
    {
        var options = OpenAI();
        options.AdditionalProperties = new() { [field] = JsonSerializer.Deserialize<JsonElement>(json) };
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoNetwork).OpenAISpeechRequestAsync(options));
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(Create(NoNetwork).OpenAISpeechStreamingAsync(options)));
    }

    [Theory]
    [InlineData("{\"encoding\":\"future_native_encoding\",\"sample_rate\":12345,\"future_output\":true}")]
    [InlineData("\"future_native_format\"")]
    [InlineData("123")]
    [InlineData("null")]
    public async Task NativeOutputFormatPassesThroughUnchangedOnAllSpeechSurfaces(string json)
    {
        var native = JsonSerializer.Deserialize<JsonElement>(json);
        var request = Request();
        request.ProviderOptions = new() { ["respeecher"] = Element(new { output_format = native }) };
        var options = OpenAI();
        options.AdditionalProperties = new() { ["output_format"] = native };
        var original = native.GetRawText();
        var provider = Create(async (message, token) =>
        {
            Assert.Equal(original, (await Body(message, token)).GetProperty("output_format").GetRawText());
            return message.RequestUri!.AbsolutePath.EndsWith("/bytes") ? Wav() : Json(Chunks);
        });
        await provider.SpeechRequest(request);
        await provider.OpenAISpeechRequestAsync(options);
        await Drain(provider.OpenAISpeechStreamingAsync(options));
        Assert.Equal(original, request.ProviderOptions["respeecher"].GetProperty("output_format").GetRawText());
        Assert.Equal(original, options.AdditionalProperties["output_format"].GetRawText());
    }

    [Fact]
    public async Task StandardPcmTranslationRequiresNativeOutputFormatObjectToPreserveItsFields()
    {
        var options = OpenAI();
        options.ResponseFormat = "pcm";
        options.AdditionalProperties = new() { ["output_format"] = Element("future_native_format") };
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(Create(NoNetwork).OpenAISpeechStreamingAsync(options)));
    }

    [Fact]
    public async Task HttpFailuresPropagateWithoutDiscoveryFallbackOrDone()
    {
        var provider = Create((_, _) => Task.FromResult(Json("denied", HttpStatusCode.Forbidden)));
        var discovery = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.Forbidden, discovery.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.OpenAISpeechRequestAsync(OpenAI()));
        var events = new List<IAudioSpeechStreamEvent>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(OpenAI()))
                events.Add(item);
        });
        Assert.Empty(events);
    }

    [Fact]
    public async Task InBandErrorAfterAudioThrowsWithNativeStatusAndNeverEmitsDone()
    {
        var provider = Create((_, _) => Task.FromResult(Json(
            "{\"type\":\"chunk\",\"data\":\"AQI=\"}\n{\"type\":\"error\",\"error\":\"failed\",\"status_code\":429,\"context_id\":\"native\"}\n")));
        var events = new List<IAudioSpeechStreamEvent>();
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(OpenAI()))
                events.Add(item);
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("native", error.Message);
        Assert.IsType<AudioSpeechStreamDelta>(Assert.Single(events));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"type\":\"chunk\"}")]
    [InlineData("{\"type\":\"chunk\",\"data\":123}")]
    [InlineData("{\"type\":\"unexpected\",\"data\":\"AQI=\"}")]
    public async Task MalformedStreamFailsWithoutDone(string body)
        => await Assert.ThrowsAsync<JsonException>(() => Drain(Create((_, _) => Task.FromResult(Json(body))).OpenAISpeechStreamingAsync(OpenAI())));

    [Fact]
    public async Task InvalidBase64AndEmptyAudioFail()
    {
        await Assert.ThrowsAsync<FormatException>(() => Drain(Create((_, _) => Task.FromResult(Json(
            "{\"type\":\"chunk\",\"data\":\"invalid\"}"))).OpenAISpeechStreamingAsync(OpenAI())));
        foreach (var body in new[] { "", "\n", "{\"type\":\"chunk\",\"data\":\"\"}" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(Create((_, _) => Task.FromResult(Json(body))).OpenAISpeechStreamingAsync(OpenAI())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) })).SpeechRequest(Request()));
    }

    [Fact]
    public async Task PreCancellationStopsAllSurfacesBeforeNetwork()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = Create(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SpeechRequest(Request(), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAISpeechRequestAsync(OpenAI(), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(provider.OpenAISpeechStreamingAsync(OpenAI(), source.Token)));
    }

    [Fact]
    public async Task MidStreamCancellationNeverEmitsDoneEvenWhenReaderHasBufferedData()
    {
        using var source = new CancellationTokenSource();
        var provider = Create((_, _) => Task.FromResult(Json(Chunks)));
        await using var enumerator = provider.OpenAISpeechStreamingAsync(OpenAI(), source.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<AudioSpeechStreamDelta>(enumerator.Current);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task CancellationInterruptsPendingNativeRead()
    {
        using var source = new CancellationTokenSource();
        var stream = new PendingStream();
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        await using var enumerator = provider.OpenAISpeechStreamingAsync(OpenAI(), source.Token).GetAsyncEnumerator();
        var pending = enumerator.MoveNextAsync().AsTask();
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task CredentialsRemainPerRequestAndPerProvider()
    {
        var observed = new List<string>();
        var handler = new Handler((message, _) =>
        {
            observed.Add(Assert.Single(message.Headers.GetValues("X-API-Key")));
            Assert.Null(message.Headers.Authorization);
            Assert.Empty(message.RequestUri!.Query);
            return Task.FromResult(message.Method == HttpMethod.Get ? Json("[]") : Wav());
        });
        var factory = new Factory(handler);
        var first = new RespeecherProvider(new Resolver("first-key"), factory);
        var second = new RespeecherProvider(new Resolver("second-key"), factory);
        await first.SpeechRequest(Request());
        await second.SpeechRequest(Request());
        await first.ListModels();
        Assert.Equal(new[] { "first-key", "second-key" }.Concat(Enumerable.Repeat("first-key", 5)), observed);
    }

    [Fact]
    public async Task HeaderScopedRegistrationResolvesAndEveryDiscoveredEntryCanExecute()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Respeecher-Key"] = "  test-key  ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("RESPEECHER"));
        Assert.Equal("X-Respeecher-Key", HeaderApiKeyResolver.SupportedProviderHeaders["respeecher"]);
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            item => item.ImplementationType == typeof(RespeecherProvider)).Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(async (message, token) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
                return Json(Voices);
            var body = await Body(message, token);
            Assert.Equal("voice/one", body.GetProperty("voice").GetProperty("id").GetString());
            Assert.Contains(message.RequestUri!.AbsolutePath,
                Models.Select(model => $"/v1/public/tts/{model}/tts/bytes"));
            return Wav();
        })));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("respeecher"));
        var provider = Assert.IsType<RespeecherProvider>(registry.GetModelProvider(scope.ServiceProvider, "respeecher"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<RespeecherProvider>(), provider);
        foreach (var model in await provider.ListModels())
            await provider.SpeechRequest(new() { Model = model.Id, Text = "Hello" });
    }

    [Fact]
    public async Task HeaderCredentialsAreIsolatedBetweenRequestSnapshots()
    {
        var firstContext = new DefaultHttpContext();
        firstContext.Request.Headers["X-Respeecher-Key"] = "first-key";
        var secondContext = new DefaultHttpContext();
        secondContext.Request.Headers["X-OpenAI-Key"] = "unrelated-key";
        var firstResolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new RequestContextAccessor(firstContext)));
        var secondResolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new RequestContextAccessor(secondContext)));
        Assert.Equal("first-key", firstResolver.Resolve("respeecher"));
        Assert.Null(secondResolver.Resolve("respeecher"));
        var missing = new RespeecherProvider(secondResolver, new Factory(new Handler(NoNetwork)));
        Assert.Empty(await missing.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SpeechRequest(Request()));
    }

    private static SpeechRequest Request() => new() { Model = "respeecher/en-rt/voice-one", Text = "Hello" };
    private static AudioSpeechRequest OpenAI() => new() { Model = "respeecher/en-rt/voice-one", Input = "Hello", StreamFormat = "sse" };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage message, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
    private static async Task<List<IAudioSpeechStreamEvent>> Drain(IAsyncEnumerable<IAudioSpeechStreamEvent> stream)
    {
        var result = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in stream)
            result.Add(item);
        return result;
    }
    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("X-API-Key")));
        Assert.Null(message.Headers.Authorization);
        Assert.Empty(message.RequestUri!.Query);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static RespeecherProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(responder)));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Wav()
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("RIFFtestWAVE")) };
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "respeecher" ? key : null;
    }
    private sealed class RequestContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
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
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
    private sealed class PendingStream : MemoryStream
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
