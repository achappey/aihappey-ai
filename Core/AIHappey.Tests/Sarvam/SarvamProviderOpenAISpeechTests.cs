using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Sarvam;

namespace AIHappey.Tests.Sarvam;

public sealed class SarvamProviderOpenAISpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_existing_sarvam_speech_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioBytes = Encoding.UTF8.GetBytes("sarvam-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        request_id = "req-1",
                        audios = new[] { Convert.ToBase64String(audioBytes) }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "sarvam/bulbul:v3",
            Voice = "shubh",
            Input = "Hello from Sarvam OpenAI compatibility!",
            ResponseFormat = "mp3"
        });

        Assert.Equal(audioBytes, audio);
        Assert.Equal("audio/mpeg", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/text-to-speech", capturedRequest.RequestUri?.AbsolutePath);
        Assert.True(capturedRequest.Headers.TryGetValues("api-subscription-key", out var values));
        Assert.Equal("test-api-key", Assert.Single(values));

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello from Sarvam OpenAI compatibility!", root.GetProperty("text").GetString());
        Assert.Equal("en-IN", root.GetProperty("target_language_code").GetString());
        Assert.Equal("shubh", root.GetProperty("speaker").GetString());
        Assert.Equal("bulbul:v3", root.GetProperty("model").GetString());
        Assert.Equal("mp3", root.GetProperty("output_audio_codec").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_sarvam_stream_request_and_maps_binary_chunks_to_openai_events()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioChunk = Encoding.UTF8.GetBytes("streamed-sarvam-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioChunk)
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "bulbul:v3",
                           Voice = "priya",
                           Input = "Hello streamed Sarvam!",
                           ResponseFormat = "mp3"
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
        Assert.Equal("/text-to-speech/stream", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Contains("audio/mpeg", capturedRequest.Headers.Accept.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues("api-subscription-key", out var values));
        Assert.Equal("test-api-key", Assert.Single(values));

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello streamed Sarvam!", root.GetProperty("text").GetString());
        Assert.Equal("en-IN", root.GetProperty("target_language_code").GetString());
        Assert.Equal("priya", root.GetProperty("speaker").GetString());
        Assert.Equal("bulbul:v3", root.GetProperty("model").GetString());
        Assert.Equal("mp3", root.GetProperty("output_audio_codec").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_sarvam_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { message = "Text exceeds maximum length of 3500 characters", code = "unprocessable_entity_error" } }),
                Encoding.UTF8,
                "application/json")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "bulbul:v3",
                               Voice = "shubh",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Sarvam streaming TTS failed (422)", exception.Message);
        Assert.Contains("Text exceeds maximum length", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_input()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "bulbul:v3"
                           }))
            {
            }
        });

        Assert.Contains("Input is required", exception.Message);
    }

    private static SarvamProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));

        return new SarvamProvider(new StaticApiKeyResolver(), httpClientFactory);
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

