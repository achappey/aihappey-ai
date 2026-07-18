using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.MurfAI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.MurfAI;

public sealed class MurfAIProviderOpenAISpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_existing_murf_speech_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioBytes = Encoding.UTF8.GetBytes("murf-mp3-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse(new
            {
                encodedAudio = Convert.ToBase64String(audioBytes)
            });
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "gen2/Gordon",
            Input = "Hello from Murf OpenAI compatibility!",
            ResponseFormat = "mp3"
        });

        Assert.Equal(audioBytes, audio);
        Assert.Equal("audio/mpeg", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/speech/generate", capturedRequest.RequestUri?.AbsolutePath);

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello from Murf OpenAI compatibility!", root.GetProperty("text").GetString());
        Assert.Equal("Gordon", root.GetProperty("voiceId").GetString());
        Assert.Equal("gen2", root.GetProperty("modelVersion").GetString());
        Assert.Equal("MP3", root.GetProperty("format").GetString());
        Assert.True(root.GetProperty("encodeAsBase64").GetBoolean());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_native_request_and_maps_raw_audio_chunks()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioBytes = Encoding.UTF8.GetBytes("murf-streamed-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioBytes)
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "murfai/falcon-2/Gordon",
                           Input = "Hello streamed Murf!",
                           ResponseFormat = "mpeg",
                           Speed = 1.2f
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first =>
            {
                var delta = Assert.IsType<AudioSpeechStreamDelta>(first);
                Assert.Equal(Convert.ToBase64String(audioBytes), delta.Audio);
            },
            second => Assert.IsType<AudioSpeechStreamDone>(second));

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/speech/stream", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Contains(capturedRequest.Headers, header => header.Key == "api-key" && header.Value.Contains("test-api-key"));

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello streamed Murf!", root.GetProperty("text").GetString());
        Assert.Equal("Gordon", root.GetProperty("voiceId").GetString());
        Assert.Equal("falcon-2", root.GetProperty("model").GetString());
        Assert.Equal("MP3", root.GetProperty("format").GetString());
        Assert.Equal(10, root.GetProperty("rate").GetInt32());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_a_voice()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "gen2",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("requires a voiceId", exception.Message);
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
                               Model = "gen2/Gordon",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("MurfAI streaming TTS failed (400): invalid voice", exception.Message);
    }

    private static HttpResponseMessage JsonResponse(object body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private static MurfAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new MurfAIProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(
                content,
                Encoding.UTF8,
                request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
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
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
