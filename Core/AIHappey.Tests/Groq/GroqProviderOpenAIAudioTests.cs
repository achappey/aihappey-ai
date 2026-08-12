using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Groq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Groq;

public sealed class GroqProviderOpenAIAudioTests
{
    [Fact]
    public async Task Speech_request_sends_OpenAI_payload_and_returns_audio_with_provider_mime_type()
    {
        HttpRequestMessage? capturedRequest = null;
        var expectedAudio = Encoding.UTF8.GetBytes("generated audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedAudio)
                {
                    Headers = { ContentType = new("audio/wav") }
                }
            };
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "playai-tts",
            Input = "Hello from Groq",
            Voice = "Fritz-PlayAI",
            ResponseFormat = "wav",
            Speed = 1.25f
        });

        Assert.Equal(expectedAudio, audio);
        Assert.Equal("audio/wav", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/openai/v1/audio/speech", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);

        using var body = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        Assert.Equal("playai-tts", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello from Groq", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("Fritz-PlayAI", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal("wav", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(1.25f, body.RootElement.GetProperty("speed").GetSingle());
        Assert.Equal("audio", body.RootElement.GetProperty("stream_format").GetString());
    }

    [Fact]
    public async Task Speech_stream_emulates_audio_delta_then_done()
    {
        var expectedAudio = Encoding.UTF8.GetBytes("generated audio");
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedAudio)
        });
        var events = new List<IAudioSpeechStreamEvent>();

        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "playai-tts",
                           Input = "Hello from Groq",
                           Voice = "Fritz-PlayAI",
                           ResponseFormat = "wav"
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first => Assert.Equal(
                Convert.ToBase64String(expectedAudio),
                Assert.IsType<AudioSpeechStreamDelta>(first).Audio),
            second => Assert.IsType<AudioSpeechStreamDone>(second));
    }

    [Fact]
    public async Task Transcription_request_sends_supported_multipart_fields_and_maps_verbose_json()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""
                {
                  "text": "hello world",
                  "language": "en",
                  "duration": 1.25,
                  "segments": [
                    { "id": 0, "seek": 0, "start": 0, "end": 1.25, "text": "hello world", "tokens": [], "temperature": 0, "avg_logprob": -0.1, "compression_ratio": 1, "no_speech_prob": 0 }
                  ]
                }
                """);
        });

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "whisper-large-v3",
            File = CreateAudioFile(Encoding.UTF8.GetBytes("fake audio")),
            Language = "en",
            Prompt = "product names",
            ResponseFormat = "verbose_json",
            Temperature = 0.2f,
            TimestampGranularities = ["segment"]
        });

        var verbose = Assert.IsType<OpenAITranscriptionVerboseResponse>(response);
        Assert.Equal("hello world", verbose.Text);
        Assert.Equal("en", verbose.Language);
        Assert.Equal(1.25, verbose.Duration);
        Assert.Single(verbose.Segments!);

        Assert.NotNull(capturedRequest);
        Assert.Equal("/openai/v1/audio/transcriptions", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("name=file", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("name=model", body);
        Assert.Contains("whisper-large-v3", body);
        Assert.Contains("name=language", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("verbose_json", body);
        Assert.Contains("name=temperature", body);
        Assert.Contains("0.2", body);
        Assert.Contains("timestamp_granularities[]", body);
        Assert.Contains("segment", body);
        Assert.DoesNotContain("name=stream", body);
    }

    [Fact]
    public async Task Transcription_stream_emulates_text_delta_then_done()
    {
        var provider = CreateProvider(_ => JsonResponse("""{ "text": "hello world" }"""));
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "whisper-large-v3-turbo",
                           File = CreateAudioFile(Encoding.UTF8.GetBytes("fake audio"))
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first => Assert.Equal("hello world", Assert.IsType<OpenAITranscriptionTextDelta>(first).Delta),
            second => Assert.Equal("hello world", Assert.IsType<OpenAITranscriptionTextDone>(second).Text));
    }

    [Fact]
    public async Task Provider_errors_include_status_and_response_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unsupported voice", Encoding.UTF8, "application/json")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
            {
                Model = "playai-tts",
                Input = "Hello",
                Voice = "missing"
            }));

        Assert.Contains("400", error.Message);
        Assert.Contains("unsupported voice", error.Message);
    }

    private static IFormFile CreateAudioFile(byte[] audio)
        => new FormFile(new MemoryStream(audio, writable: false), 0, audio.Length, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

    private static GroqProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var factory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new GroqProvider(new StaticApiKeyResolver(), cache, factory);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            clone.Content = new ByteArrayContent(content);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
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
