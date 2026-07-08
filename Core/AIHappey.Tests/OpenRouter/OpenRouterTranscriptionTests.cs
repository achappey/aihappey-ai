using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenRouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenRouter;

public sealed class OpenRouterTranscriptionTests
{
    [Fact]
    public async Task Transcription_request_returns_exact_json_request_body()
    {
        string? capturedBody = null;

        var provider = CreateProvider(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse("""
                {
                  "text": "hello from openrouter",
                  "usage": { "seconds": 1.5 }
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "openai/whisper-1",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["openrouter"] = JsonSerializer.SerializeToElement(new
                {
                    language = "en"
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(capturedBody);
        Assert.NotNull(response.Request);
        Assert.Equal(capturedBody, response.Request!.Body);
        Assert.Contains("\"model\":\"openai/whisper-1\"", response.Request.Body);
        Assert.Contains("\"language\":\"en\"", response.Request.Body);
        Assert.Equal("hello from openrouter", response.Text);
    }

    private static OpenRouterProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.ai/api/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new OpenRouterProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
