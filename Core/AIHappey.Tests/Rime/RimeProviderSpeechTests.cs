using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Rime;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Rime;

public sealed class RimeProviderSpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_existing_rime_speech_request_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wav-audio"))
            };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "rime/mistv3/cove",
            Input = "Hello from OpenAI compatibility!",
            ResponseFormat = "wav"
        });

        Assert.Equal(Encoding.UTF8.GetBytes("wav-audio"), audio);
        Assert.Equal("audio/wav", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://mist.api.rime.ai/", capturedRequest.RequestUri?.AbsoluteUri);
        Assert.Equal("audio/wav", capturedRequest.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("mistv3", root.GetProperty("modelId").GetString());
        Assert.Equal("cove", root.GetProperty("speaker").GetString());
        Assert.Equal("Hello from OpenAI compatibility!", root.GetProperty("text").GetString());
        Assert.Equal("en", root.GetProperty("lang").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_rime_streaming_request_and_maps_raw_audio_to_openai_events()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioChunk = Encoding.UTF8.GetBytes("streamed-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioChunk)
            };
            response.Content.Headers.ContentType = new("audio/webm");
            return response;
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "mistv3",
                           Voice = "cove",
                           Input = "Hello streamed Rime!",
                           ResponseFormat = "webm"
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first =>
            {
                var delta = Assert.IsType<AudioSpeechStreamDelta>(first);
                Assert.Equal("speech.audio.delta", delta.Type);
                Assert.Equal(Convert.ToBase64String(audioChunk), delta.Audio);
            },
            second =>
            {
                var done = Assert.IsType<AudioSpeechStreamDone>(second);
                Assert.Equal("speech.audio.done", done.Type);
                Assert.Null(done.Usage);
            });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://mist.api.rime.ai/", capturedRequest.RequestUri?.AbsoluteUri);
        Assert.Contains("audio/webm", capturedRequest.Headers.Accept.ToString());
        Assert.Contains("codecs=opus", capturedRequest.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("mistv3", root.GetProperty("modelId").GetString());
        Assert.Equal("cove", root.GetProperty("speaker").GetString());
        Assert.Equal("Hello streamed Rime!", root.GetProperty("text").GetString());
        Assert.Equal("en", root.GetProperty("lang").GetString());
        Assert.Equal(24000, root.GetProperty("samplingRate").GetInt32());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_rime_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid voice", Encoding.UTF8, "text/plain")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "coda/astra",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Rime TTS failed (400): invalid voice", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_allows_provider_default_voice_for_base_model()
    {
        var provider = CreateProvider(request =>
        {
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.False(payload.RootElement.TryGetProperty("speaker", out _));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) };
        });
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest { Model = "coda", Input = "Hello!" }))
            events.Add(item);
        Assert.IsType<AudioSpeechStreamDone>(events[^1]);
    }

    [Fact]
    public async Task Coda_uses_streaming_accept_header_time_scale_factor_and_base64_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "coda/astra",
            Text = "Hello from Rime!",
            OutputFormat = null,
            ProviderOptions = ProviderOptions(new
            {
                language = "en",
                samplingRate = 24000,
                timeScaleFactor = 1.15f,
                speedAlpha = 0.5f
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://coda.api.rime.ai/", capturedRequest.RequestUri?.AbsoluteUri);
        Assert.Equal("audio/mpeg", capturedRequest.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("astra", root.GetProperty("speaker").GetString());
        Assert.Equal("Hello from Rime!", root.GetProperty("text").GetString());
        Assert.Equal("coda", root.GetProperty("modelId").GetString());
        Assert.Equal("en", root.GetProperty("lang").GetString());
        Assert.Equal(24000, root.GetProperty("samplingRate").GetInt32());
        Assert.Equal(1.15f, root.GetProperty("timeScaleFactor").GetSingle(), precision: 2);
        Assert.False(root.TryGetProperty("language", out _));
        Assert.False(root.TryGetProperty("audioFormat", out _));
        Assert.False(root.TryGetProperty("speedAlpha", out _));

        Assert.Equal("AQID", response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("mp3", response.Audio.Format);
        Assert.Contains(response.Warnings, warning => warning.ToString()!.Contains("providerOptions.rime.speedAlpha", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MistV3_uses_lang_pause_inline_speed_and_wav_accept_header()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wav-audio"))
            };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "rime/mistv3/cove",
            Text = "Hello from Mist v3!",
            OutputFormat = "wav",
            ProviderOptions = ProviderOptions(new
            {
                timeScaleFactor = 0.8f,
                pauseBetweenBrackets = true,
                inlineSpeedAlpha = "0.5, 3"
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("audio/wav", capturedRequest!.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("mistv3", root.GetProperty("modelId").GetString());
        Assert.Equal("cove", root.GetProperty("speaker").GetString());
        Assert.Equal("en", root.GetProperty("lang").GetString());
        Assert.Equal(0.8f, root.GetProperty("timeScaleFactor").GetSingle(), precision: 2);
        Assert.True(root.GetProperty("pauseBetweenBrackets").GetBoolean());
        Assert.Equal("0.5, 3", root.GetProperty("inlineSpeedAlpha").GetString());
        Assert.False(root.TryGetProperty("speedAlpha", out _));
        Assert.False(root.TryGetProperty("audioFormat", out _));

        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("wav-audio")), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
    }

    [Fact]
    public async Task Speech_request_forwards_unknown_provider_options_unchanged_at_the_upstream_root()
    {
        JsonElement payload = default;
        var providerOptions = ParseElement("""
            {
              "language": "de",
              "samplingRate": 16000,
              "timeScaleFactor": 1.2,
              "futureObject": {"nested": [{"value": 1}, [true, null, "text"]]},
              "futureArray": [1, {"more": [false, 2.5]}, []],
              "futureString": "unchanged",
              "futureNumber": 12345678901234567890.123456789,
              "futureBoolean": false,
              "futureNull": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "coda",
            Text = "Hello!",
            ProviderOptions = new() { ["rime"] = providerOptions }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(providerOptions, clientBoundary.GetProperty("providerOptions").GetProperty("rime")));
        var provider = CreateProvider(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2])
            };
        });

        await provider.SpeechRequest(request);

        Assert.Equal("de", payload.GetProperty("lang").GetString());
        Assert.Equal(16000, payload.GetProperty("samplingRate").GetInt32());
        Assert.Equal(1.2f, payload.GetProperty("timeScaleFactor").GetSingle());
        foreach (var name in new[]
                 {
                     "futureObject", "futureArray", "futureString", "futureNumber", "futureBoolean", "futureNull"
                 })
            Assert.True(JsonElement.DeepEquals(providerOptions.GetProperty(name), payload.GetProperty(name)), name);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("rime", out _));
        Assert.Equal(11, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task MistV2_is_rejected_instead_of_silently_falling_back_to_another_model()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "rime/mistv2/astra", Text = "Hello from Mist v2!"
        }));
        Assert.Contains("Use coda or mistv3", error.Message);
    }

    private static RimeProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new RimeProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static Dictionary<string, JsonElement> ProviderOptions(object metadata)
        => new()
        {
            ["rime"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
