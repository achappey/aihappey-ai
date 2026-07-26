using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.HumeAI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.HumeAI;

public sealed class HumeAIProviderOpenAISpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_delegates_to_json_synthesis_and_decodes_audio()
    {
        HttpRequestMessage? captured = null;
        var audio = Encoding.UTF8.GetBytes("hume-audio");
        var provider = CreateProvider(request =>
        {
            captured = CloneRequest(request);
            return JsonResponse($$"""{"generations":[{"audio":"{{Convert.ToBase64String(audio)}}","generation_id":"generation-1"}],"request_id":"request-1"}""");
        });

        var result = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "octave/HUME_AI/voice-1",
            Input = "Hello Hume",
            ResponseFormat = "mp3",
            Instructions = "Warm and friendly",
            Speed = 1.1f
        });

        Assert.Equal(audio, result.Audio);
        Assert.Equal("audio/mpeg", result.MimeType);
        Assert.Equal("/v0/tts", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("test-key", captured.Headers.GetValues("X-Hume-Api-Key").Single());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_maps_audio_events_ignores_timestamps_and_sends_shared_payload()
    {
        HttpRequestMessage? captured = null;
        var first = Convert.ToBase64String(Encoding.UTF8.GetBytes("first"));
        var second = Convert.ToBase64String(Encoding.UTF8.GetBytes("second"));
        var provider = CreateProvider(request =>
        {
            captured = CloneRequest(request);
            var sse = $$$"""
                data: {"type":"audio","audio":"{{{first}}}","audio_format":"mp3"}

                data: {"type":"timestamp","timestamp":{"text":"Hello"}}

                data: {"type":"audio",
                data: "audio":"{{{second}}}","audio_format":"mp3"}

                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "octave/HUME_AI/voice-1",
                           Voice = "ignored-by-shortcut",
                           Input = "Stream this",
                           ResponseFormat = "mp3",
                           Instructions = "Calm",
                           Speed = 0.9f,
                           StreamFormat = "sse"
                       }))
            events.Add(item);

        Assert.Collection(events,
            item => Assert.Equal(first, Assert.IsType<AudioSpeechStreamDelta>(item).Audio),
            item => Assert.Equal(second, Assert.IsType<AudioSpeechStreamDelta>(item).Audio),
            item => Assert.IsType<AudioSpeechStreamDone>(item));

        Assert.Equal("/v0/tts/stream/json", captured!.RequestUri!.AbsolutePath);
        Assert.Contains("text/event-stream", captured.Headers.Accept.ToString());
        Assert.Equal("test-key", captured.Headers.GetValues("X-Hume-Api-Key").Single());
        using var document = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("instant_mode").GetBoolean());
        Assert.Equal("mp3", root.GetProperty("format").GetProperty("type").GetString());
        var utterance = root.GetProperty("utterances")[0];
        Assert.Equal("Stream this", utterance.GetProperty("text").GetString());
        Assert.Equal("Calm", utterance.GetProperty("description").GetString());
        Assert.Equal("voice-1", utterance.GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("HUME_AI", utterance.GetProperty("voice").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_sse_stream_format()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "octave/HUME_AI/voice-1",
                               Input = "Hello"
                           }))
            {
            }
        });

        Assert.Contains("stream_format 'sse'", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_rejects_invalid_base64()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"audio\",\"audio\":\"not base64!\"}\n\n", Encoding.UTF8, "text/event-stream")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(StreamRequest()))
            {
            }
        });

        Assert.Contains("invalid base64", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_includes_upstream_http_error()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("invalid voice")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(StreamRequest()))
            {
            }
        });

        Assert.Contains("HumeAI streaming TTS failed (422): invalid voice", exception.Message);
    }

    private static AudioSpeechRequest StreamRequest() => new()
    {
        Model = "octave/HUME_AI/voice-1",
        Input = "Hello",
        StreamFormat = "sse"
    };

    private static HumeAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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
