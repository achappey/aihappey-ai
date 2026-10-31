using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.StepFun;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.StepFun;

public sealed class StepFunProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequestForwardsCurrentProviderMetadataShape()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/audio/speech", request.RequestUri?.AbsolutePath);
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return BinaryResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepfun/stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions("""
                {
                  "volume": 1.2,
                  "sample_rate": 48000,
                  "voice": "metadata-voice",
                  "response_format": "opus",
                  "speed": 1.1,
                  "voice_label": {
                    "emotion": "friendly",
                    "style": "calm",
                    "future_label": {"nested": [true, null]}
                  },
                  "pronunciation_map": {
                    "tone": [" LOL/laugh out loudly ", "你好/ni3 hao3"],
                    "future_map": [{"value": "unchanged"}]
                  }
                }
                """)
        });

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("stepaudio-2.5-tts", root.GetProperty("model").GetString());
        Assert.Equal("zixinnansheng", root.GetProperty("voice").GetString());
        Assert.Equal("opus", root.GetProperty("response_format").GetString());
        Assert.Equal(1.1, root.GetProperty("speed").GetSingle(), 3);
        Assert.Equal(1.2, root.GetProperty("volume").GetDouble(), 3);
        Assert.Equal(48000, root.GetProperty("sample_rate").GetInt32());
        Assert.Equal("friendly", root.GetProperty("voice_label").GetProperty("emotion").GetString());
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"nested":[true,null]}"""),
            root.GetProperty("voice_label").GetProperty("future_label")));

        var pronunciationMap = root.GetProperty("pronunciation_map");
        Assert.Equal(JsonValueKind.Object, pronunciationMap.ValueKind);
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""[{"value":"unchanged"}]"""),
            pronunciationMap.GetProperty("future_map")));
        var tones = pronunciationMap.GetProperty("tone").EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Collection(
            tones,
            tone => Assert.Equal("LOL/laugh out loudly", tone),
            tone => Assert.Equal("你好/ni3 hao3", tone));
    }

    [Fact]
    public async Task SpeechRequestFieldsOverrideProviderMetadata()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return BinaryResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/model-voice",
            Text = "hello",
            Voice = "request-voice",
            Speed = 1.5f,
            OutputFormat = "wav",
            ProviderOptions = ProviderOptions("""
                {"voice":"metadata-voice","speed":0.75,"response_format":"opus"}
                """)
        });

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("model-voice", root.GetProperty("voice").GetString());
        Assert.Equal(1.5f, root.GetProperty("speed").GetSingle());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task SpeechRequestOmitsUnsetAndEmptyProviderMetadata()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return BinaryResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions("""{"pronunciation_map":{"tone":[" "]}}""")
        });

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("volume", out _));
        Assert.False(root.TryGetProperty("sample_rate", out _));
        Assert.False(root.TryGetProperty("pronunciation_map", out _));
    }

    [Theory]
    [InlineData(0.09)]
    [InlineData(2.01)]
    public async Task SpeechRequestRejectsOutOfRangeVolume(double volume)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions(JsonSerializer.Serialize(new { volume }))
        }));
    }

    [Fact]
    public async Task SpeechRequestRejectsUnsupportedSampleRate()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions("""{"sample_rate":44100}""")
        }));
    }

    [Fact]
    public async Task SpeechRequestForwardsUnknownMetadataAtTheRootWithoutChangingProviderOptions()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return BinaryResponse();
        });
        var requestOptions = ParseElement("""
            {
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var otherProvider = ParseElement("""{"secret":"not-forwarded"}""");
        var providerOptions = new Dictionary<string, JsonElement>
        {
            ["stepfun"] = requestOptions,
            ["another-provider"] = otherProvider
        };

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = providerOptions
        });

        using var document = JsonDocument.Parse(body!);
        var payload = document.RootElement;
        foreach (var property in requestOptions.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        Assert.Equal("stepaudio-2.5-tts", payload.GetProperty("model").GetString());
        Assert.Equal("hello", payload.GetProperty("input").GetString());
        Assert.Equal("zixinnansheng", payload.GetProperty("voice").GetString());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("stepfun", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "future_array", "future_boolean", "future_null", "future_number", "future_object",
                "future_string", "input", "model", "response_format", "voice"
            },
            payload.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.True(JsonElement.DeepEquals(requestOptions, providerOptions["stepfun"]));
        Assert.True(JsonElement.DeepEquals(otherProvider, providerOptions["another-provider"]));
    }

    [Fact]
    public async Task SpeechRequestPreservesJsonTypeErrorsForKnownMetadata()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions("""{"volume":"loud"}""")
        }));
    }

    [Fact]
    public async Task SpeechRequestPreservesUpstreamErrorBehavior()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello"
        }));

        Assert.Equal("StepFun speech request failed (400): upstream error", error.Message);
    }

    private static StepFunProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static Dictionary<string, JsonElement> ProviderOptions(string metadata)
        => new()
        {
            ["stepfun"] = ParseElement(metadata)
        };

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage BinaryResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
            {
                Headers = { ContentType = new("audio/mpeg") }
            }
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
