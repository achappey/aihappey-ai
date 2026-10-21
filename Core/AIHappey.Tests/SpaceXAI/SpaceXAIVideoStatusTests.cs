using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SpaceXAI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SpaceXAI;

public sealed class SpaceXAIVideoStatusTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("processing")]
    public async Task GetVideoOperationStatus_maps_in_progress_statuses_to_pending(string status)
    {
        var provider = CreateProvider(_ => JsonResponse($$"""
            { "status": "{{status}}" }
            """));

        var result = await provider.GetVideoOperationStatus("request-id");

        Assert.IsType<VideoOperationPendingResult>(result);
    }

    private static SpaceXAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.x.ai/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new SpaceXAIProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
