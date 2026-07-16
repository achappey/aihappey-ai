using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.FishAudio;

namespace AIHappey.Tests.FishAudio;

public sealed class FishAudioProviderOpenAISpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_native_fish_audio_speech_request_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("mp3-audio"))
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "fishaudio/s2-pro",
            Voice = "voice-reference-id",
            Input = "Hello from FishAudio OpenAI compatibility!",
            ResponseFormat = "mp3",
            Speed = 1.15f
        });

        Assert.Equal(Encoding.UTF8.GetBytes("mp3-audio"), audio);
        Assert.Equal("audio/mpeg", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/tts", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("s2-pro", capturedRequest.Headers.GetValues("model").Single());
        Assert.Contains("audio/*", capturedRequest.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello from FishAudio OpenAI compatibility!", root.GetProperty("text").GetString());
        Assert.Equal("voice-reference-id", root.GetProperty("reference_id").GetString());
        Assert.Equal("mp3", root.GetProperty("format").GetString());
        Assert.Equal(1.15f, root.GetProperty("prosody").GetProperty("speed").GetSingle(), precision: 2);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_timestamp_stream_request_and_maps_audio_base64_to_openai_events()
    {
        HttpRequestMessage? capturedRequest = null;
        var firstChunk = Convert.ToBase64String(Encoding.UTF8.GetBytes("first-audio"));
        var secondChunk = Convert.ToBase64String(Encoding.UTF8.GetBytes("second-audio"));
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);

            var sse = $$"""
                      data: {"audio_base64":"{{firstChunk}}","content":"Hello","alignment":null,"chunk_seq":0,"chunk_audio_offset_sec":0}

                      data: {"audio_base64":"{{secondChunk}}","content":"world","alignment":{"audio_duration":0.5,"segments":[{"text":"world","start":0,"end":0.5}]},"chunk_seq":0,"chunk_audio_offset_sec":0}

                      """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "fishaudio/s2.1-pro-free",
                           Voice = "voice-reference-id",
                           Input = "Hello streamed FishAudio!",
                           ResponseFormat = "opus",
                           Speed = 0.95f
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
        Assert.Equal("/v1/tts/stream/with-timestamp", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("s2.1-pro-free", capturedRequest.Headers.GetValues("model").Single());
        Assert.Contains("text/event-stream", capturedRequest.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello streamed FishAudio!", root.GetProperty("text").GetString());
        Assert.Equal("voice-reference-id", root.GetProperty("reference_id").GetString());
        Assert.Equal("opus", root.GetProperty("format").GetString());
        Assert.Equal("balanced", root.GetProperty("latency").GetString());
        Assert.Equal(0.95f, root.GetProperty("prosody").GetProperty("speed").GetSingle(), precision: 2);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_voice_reference_id()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "fishaudio/s2-pro",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("FishAudio voice is required", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_fish_audio_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid reference_id", Encoding.UTF8, "text/plain")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "fishaudio/s2-pro",
                               Voice = "bad-reference-id",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("FishAudio streaming TTS failed (400): invalid reference_id", exception.Message);
    }

    private static FishAudioProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));

        return new FishAudioProvider(new StaticApiKeyResolver(), httpClientFactory);
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
