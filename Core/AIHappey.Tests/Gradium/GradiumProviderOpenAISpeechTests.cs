using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Gradium;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Gradium;

public sealed class GradiumProviderOpenAISpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_native_gradium_speech_request_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioBytes = Encoding.UTF8.GetBytes("gradium-wav-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioBytes)
            };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "default/YTpq7expH9539ERJ",
            Input = "Hello from Gradium OpenAI compatibility!",
            ResponseFormat = "wav"
        });

        Assert.Equal(audioBytes, audio);
        Assert.Equal("audio/wav", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/api/post/speech/tts", capturedRequest.RequestUri?.AbsolutePath);

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello from Gradium OpenAI compatibility!", root.GetProperty("text").GetString());
        Assert.Equal("YTpq7expH9539ERJ", root.GetProperty("voice_id").GetString());
        Assert.Equal("wav", root.GetProperty("output_format").GetString());
        Assert.True(root.GetProperty("only_audio").GetBoolean());
        Assert.False(root.TryGetProperty("model_name", out _));
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_json_stream_request_and_maps_audio_events()
    {
        HttpRequestMessage? capturedRequest = null;
        var firstChunk = Convert.ToBase64String(Encoding.UTF8.GetBytes("chunk-one"));
        var secondChunk = Convert.ToBase64String(Encoding.UTF8.GetBytes("chunk-two"));
        var streamBody = string.Join('\n',
            JsonSerializer.Serialize(new { type = "ready", request_id = "req-1", sample_rate = 48000 }),
            JsonSerializer.Serialize(new { type = "text", text = "Hello", start_s = 0.0, stop_s = 0.2 }),
            JsonSerializer.Serialize(new { type = "audio", audio = firstChunk, start_s = 0.0, stop_s = 0.08 }),
            JsonSerializer.Serialize(new { type = "audio", audio = secondChunk, start_s = 0.08, stop_s = 0.16 }),
            JsonSerializer.Serialize(new { type = "end_of_stream" }));

        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamBody, Encoding.UTF8, "application/x-ndjson")
            };
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "default",
                           Voice = "YTpq7expH9539ERJ",
                           Input = "Hello streamed Gradium!",
                           ResponseFormat = "pcm"
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
                Assert.Equal(firstChunk, delta.Audio);
            },
            second =>
            {
                var delta = Assert.IsType<AudioSpeechStreamDelta>(second);
                Assert.Equal("speech.audio.delta", delta.Type);
                Assert.Equal(secondChunk, delta.Audio);
            },
            third =>
            {
                var done = Assert.IsType<AudioSpeechStreamDone>(third);
                Assert.Equal("speech.audio.done", done.Type);
                Assert.Null(done.Usage);
            });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/api/post/speech/tts", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Contains(capturedRequest.Headers.Accept, header => header.MediaType == "application/x-ndjson");

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello streamed Gradium!", root.GetProperty("text").GetString());
        Assert.Equal("YTpq7expH9539ERJ", root.GetProperty("voice_id").GetString());
        Assert.Equal("pcm", root.GetProperty("output_format").GetString());
        Assert.False(root.GetProperty("only_audio").GetBoolean());
        Assert.False(root.TryGetProperty("model_name", out _));
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_uses_model_suffix_voice_and_custom_model_name()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { type = "end_of_stream" }), Encoding.UTF8, "application/x-ndjson")
            };
        });

        await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "gradium/experimental/custom-voice",
                           Voice = "ignored-voice",
                           Input = "Hello custom model!",
                           ResponseFormat = "wave"
                       }))
        {
        }

        Assert.NotNull(capturedRequest);
        using var document = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("experimental", root.GetProperty("model_name").GetString());
        Assert.Equal("custom-voice", root.GetProperty("voice_id").GetString());
        Assert.Equal("wav", root.GetProperty("output_format").GetString());
        Assert.False(root.GetProperty("only_audio").GetBoolean());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_voice_for_base_model()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "default",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Voice is required for Gradium speech requests", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_http_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid voice", Encoding.UTF8, "text/plain")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "default",
                               Voice = "missing_voice",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Gradium streaming TTS failed (400): invalid voice", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_json_stream_error_message()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { type = "error", code = 1008, message = "API key is revoked" }),
                Encoding.UTF8,
                "application/x-ndjson")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "default/YTpq7expH9539ERJ",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Gradium streaming TTS failed: 1008: API key is revoked", exception.Message);
    }

    private static GradiumProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new GradiumProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
