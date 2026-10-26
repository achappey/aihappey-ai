using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Narakeet;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Narakeet;

public sealed class NarakeetProviderTests
{
    private const string Voices = """
        [
          {"name":"mickey","language":"English (American)","languageCode":"en-US","styles":["cheerful"]},
          {"name":"voice / two","language":"French","languageCode":"fr-FR","styles":[]},
          {"name":"mickey","languageCode":"en-US"}
        ]
        """;

    [Fact]
    public async Task DiscoveryPreservesNativeIdentifiersAndMapsVoiceFields()
    {
        var models = (await CreateProvider((message, _) =>
        {
            AssertAuth(message);
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal("https://api.narakeet.com/voices", message.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(Voices));
        }).ListModels()).ToArray();
        Assert.Equal(["narakeet/mickey", "narakeet/voice / two"], models.Select(m => m.Id));
        Assert.All(models, m =>
        {
            Assert.Equal("speech", m.Type);
            Assert.Equal("Narakeet", m.OwnedBy);
            Assert.Null(m.Pricing);
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":1}]")]
    [InlineData("[{\"name\":\" \"}]")]
    public async Task MalformedDiscoveryFailsWithoutInventingEntries(string json)
        => await Assert.ThrowsAsync<JsonException>(() => CreateProvider((_, _) => Task.FromResult(JsonResponse(json))).ListModels());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeySkipsDiscoveryAndRejectsSpeech(string? key)
    {
        var provider = CreateProvider(NoNetwork, key);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
    }

    [Fact]
    public async Task SpeechMapsTextAndQueryParametersWithoutMutatingNativeOptions()
    {
        var request = Request();
        request.Voice = "voice / two";
        request.OutputFormat = "m4a";
        request.Speed = 1.25f;
        request.ProviderOptions = new()
        {
            ["narakeet"] = Element(new Dictionary<string, object>
            {
                ["voice"] = "ignored",
                ["voice-speed"] = 0.8,
                ["voice-volume"] = "soft",
                ["future-option"] = "a&b=漢字",
                ["flag"] = true
            }),
            ["other"] = Element(new { ignored = true })
        };
        var original = request.ProviderOptions["narakeet"].GetRawText();
        var response = await CreateProvider(async (message, token) =>
        {
            AssertAuth(message);
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/text-to-speech/m4a", message.RequestUri!.AbsolutePath);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(message.RequestUri.Query);
            Assert.Equal("voice / two", query["voice"].ToString());
            Assert.Equal("1.25", query["voice-speed"].ToString());
            Assert.Equal("soft", query["voice-volume"].ToString());
            Assert.Equal("a&b=漢字", query["future-option"].ToString());
            Assert.Equal("true", query["flag"].ToString());
            Assert.False(query.ContainsKey("ignored"));
            Assert.Equal("text/plain", message.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("Hello 世界", await message.Content.ReadAsStringAsync(token));
            Assert.Equal("application/octet-stream", Assert.Single(message.Headers.Accept).MediaType);
            return AudioResponse([1, 2, 3]);
        }).SpeechRequest(request);
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal("m4a", response.Audio.Format);
        Assert.Equal("audio/mp4", response.Audio.MimeType);
        Assert.Equal("narakeet/voice / two", response.Response.ModelId);
        Assert.Equal("request-1", response.Response.Headers!["x-request-id"]);
        Assert.Equal("3", response.ProviderMetadata!["narakeet"].GetProperty("headers").GetProperty("x-duration-seconds").GetString());
        Assert.Equal(original, request.ProviderOptions["narakeet"].GetRawText());
    }

    [Fact]
    public async Task OpenAIUsesNativeOptionsAndDiscoveredVoiceWithoutSynthesisDefaults()
    {
        var provider = CreateProvider(async (message, token) =>
        {
            Assert.Equal("/text-to-speech/mp3", message.RequestUri!.AbsolutePath);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(message.RequestUri.Query);
            Assert.Equal(2, query.Count);
            Assert.Equal("mickey", query["voice"].ToString());
            Assert.Equal("0.8", query["voice-speed"].ToString());
            Assert.Equal("Hello", await message.Content!.ReadAsStringAsync(token));
            return AudioResponse([1, 2]);
        });
        var response = await provider.OpenAISpeechRequestAsync(new()
        {
            Model = "narakeet/mickey", Input = "Hello",
            AdditionalProperties = new() { ["voice-speed"] = Element(0.8) }
        });
        Assert.Equal(new byte[] { 1, 2 }, response.Audio);
        Assert.Equal("audio/mpeg", response.MimeType);
    }

    [Fact]
    public async Task StreamingPreservesAudioAndDoesNotInventUsage()
    {
        var audio = Enumerable.Range(0, 20000).Select(i => (byte)i).ToArray();
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var part in CreateProvider((_, _) => Task.FromResult(AudioResponse(audio)))
            .OpenAISpeechStreamingAsync(StreamRequest()))
            events.Add(part);
        Assert.True(events.OfType<AudioSpeechStreamDelta>().Count() > 1);
        Assert.Equal(audio, events.OfType<AudioSpeechStreamDelta>().SelectMany(p => Convert.FromBase64String(p.Audio)).ToArray());
        Assert.Null(Assert.IsType<AudioSpeechStreamDone>(events[^1]).Usage);
        Assert.Single(events.OfType<AudioSpeechStreamDone>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ErrorsPreserveStatusAndNativeBody(HttpStatusCode status)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""{"error":"native failure"}""", status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("native failure", error.Message);
        error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(Request()));
        Assert.Equal(status, error.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(StreamRequest())) { }
        });
    }

    [Fact]
    public async Task EmptyAudioAndInvalidFieldsFailExplicitly()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(AudioResponse([])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(StreamRequest())) { }
        });
        provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.SpeechRequest(null!));
        foreach (var format in new[] { "wav", "pcm", "../mp3", "" })
        {
            var request = Request();
            request.OutputFormat = format;
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        }
        var invalid = Request();
        invalid.Instructions = "whisper";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(invalid));
        invalid = Request();
        invalid.Language = "en";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(invalid));
        invalid = Request();
        invalid.ProviderOptions = new() { ["narakeet"] = Element(new { option = new { unsupported = true } }) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(invalid));
        invalid.ProviderOptions["narakeet"] = Element("invalid");
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(invalid));
        foreach (var model in new[] { "", " ", "narakeet/" })
        {
            invalid = Request();
            invalid.Model = model;
            await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(invalid));
        }
        var streamRequest = StreamRequest();
        streamRequest.StreamFormat = "audio";
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(streamRequest)) { }
        });
    }

    [Fact]
    public async Task CancellationPropagatesBeforeRequestsAndBetweenChunks()
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
        streaming.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task HeaderScopedRegistrationMakesEveryDiscoveredVoiceExecutable()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Narakeet-Key"] = " test-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("NARAKEET"));
        Assert.Equal("X-Narakeet-Key", HeaderApiKeyResolver.SupportedProviderHeaders["narakeet"]);
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ImplementationType == typeof(NarakeetProvider)).Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler((message, _) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(Voices));
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(message.RequestUri!.Query);
            Assert.Contains(query["voice"].ToString(), new[] { "mickey", "voice / two" });
            return Task.FromResult(AudioResponse([1]));
        })));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("narakeet"));
        var provider = Assert.IsType<NarakeetProvider>(registry.GetModelProvider(scope.ServiceProvider, "narakeet"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<NarakeetProvider>(), provider);
        foreach (var model in await provider.ListModels())
            await provider.SpeechRequest(new() { Model = model.Id, Text = "Hello" });
    }

    private static SpeechRequest Request() => new() { Model = "narakeet/mickey", Text = "Hello 世界" };
    private static AudioSpeechRequest StreamRequest() => new() { Model = "narakeet/mickey", Input = "Hello", StreamFormat = "sse" };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __) => throw new InvalidOperationException("Unexpected network request.");
    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("x-api-key")));
        Assert.Null(message.Headers.Authorization);
    }
    private static NarakeetProvider CreateProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(handler)));
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage AudioResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.Add("x-request-id", "request-1");
        response.Headers.Add("x-duration-seconds", "3");
        return response;
    }
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "narakeet" ? key : null;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
