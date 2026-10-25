using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SpaceXAI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SpaceXAI;

public sealed class SpaceXAIModelDiscoveryTests
{
    [Fact]
    public async Task Model_discovery_maps_documented_context_length()
    {
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/v1/models" => JsonResponse("""
                {
                  "data": [
                    { "id": "grok-context", "context_length": 131072, "created": 1760000000, "owned_by": "xai" },
                    { "id": "grok-without-context", "created": 1760000001, "owned_by": "xai" }
                  ]
                }
                """),
            "/v1/tts/voices" => JsonResponse("""{"voices":[]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Name);

        Assert.Equal(131072, models["grok-context"].ContextWindow);
        Assert.Null(models["grok-without-context"].ContextWindow);
    }

    private static SpaceXAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        var httpClientFactory = new StaticHttpClientFactory(httpClient);
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
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
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
