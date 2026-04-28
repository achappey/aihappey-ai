using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.xAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.XAI;

public sealed class XAITranscriptionTests
{
    [Fact]
    public async Task Transcription_request_passes_provider_options_and_adds_file_last()
    {
        HttpRequestMessage? capturedRequest = null;

        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""
                {
                  "text": "hello world",
                  "language": "",
                  "duration": 1.25,
                  "words": [
                    { "text": "hello", "start": 0, "end": 0.5, "confidence": 0.9 },
                    { "text": "world", "start": 0.5, "end": 1.25, "confidence": 0.8 }
                  ]
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "xai/stt",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["xai"] = JsonSerializer.SerializeToElement(new
                {
                    language = "en",
                    format = true,
                    diarize = true,
                    audio_format = "wav"
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/stt", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("name=language", body);
        Assert.Contains("en", body);
        Assert.Contains("name=format", body);
        Assert.Contains("true", body);
        Assert.Contains("name=diarize", body);
        Assert.Contains("name=audio_format", body);

        Assert.True(body.LastIndexOf("name=file", StringComparison.Ordinal) > body.LastIndexOf("name=audio_format", StringComparison.Ordinal));

        Assert.Equal("hello world", response.Text);
        Assert.Null(response.Language);
        Assert.Equal(1.25f, response.DurationInSeconds);
        Assert.Equal(2, response.Segments.Count());
        Assert.Equal("hello", response.Segments.First().Text);
    }

    [Fact]
    public async Task Transcription_request_supports_url_only_provider_option()
    {
        HttpRequestMessage? capturedRequest = null;

        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""
                {
                  "text": "from url",
                  "language": "en",
                  "duration": 2.0
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "xai/stt",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["xai"] = JsonSerializer.SerializeToElement(new
                {
                    url = "https://example.com/audio.mp3",
                    language = "en"
                }, JsonSerializerOptions.Web)
            }
        });

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("name=url", body);
        Assert.Contains("https://example.com/audio.mp3", body);
        Assert.DoesNotContain("name=file", body);
        Assert.Equal("from url", response.Text);
        Assert.Equal("en", response.Language);
    }

    [Fact]
    public async Task Transcription_request_requires_sample_rate_for_raw_audio_format()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "xai/stt",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["xai"] = JsonSerializer.SerializeToElement(new
                {
                    audio_format = "pcm"
                }, JsonSerializerOptions.Web)
            }
        }));
    }

    private static XAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.x.ai/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new XAIProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "text/plain");
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
