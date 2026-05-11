using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cohere;

public sealed class CohereTranscriptionTests
{
    [Fact]
    public async Task Transcription_request_posts_multipart_to_cohere_v2_endpoint()
    {
        HttpRequestMessage? capturedRequest = null;

        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""
                {
                  "text": "hello from cohere"
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "cohere-transcribe-03-2026",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cohere"] = JsonSerializer.SerializeToElement(new
                {
                    language = "en",
                    temperature = 0.2f
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v2/audio/transcriptions", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("name=model", body);
        Assert.Contains("cohere-transcribe-03-2026", body);
        Assert.Contains("name=language", body);
        Assert.Contains("en", body);
        Assert.Contains("name=temperature", body);
        Assert.Contains("0.2", body);
        Assert.Contains("name=file", body);

        Assert.Equal("hello from cohere", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Empty(response.Segments);
        Assert.Equal("cohere-transcribe-03-2026", response.Response.ModelId);
    }

    [Fact]
    public async Task Transcription_request_requires_cohere_language_provider_option()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "cohere-transcribe-03-2026",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav"
        }));

        Assert.Contains("providerOptions.cohere.language", ex.Message);
    }

    private static CohereProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cohere.com/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new CohereProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
