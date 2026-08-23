using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.DeepInfra;

public sealed class DeepInfraProviderVideoTests
{
  
    [Fact]
    public async Task ListModels_classifies_video_models()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v1/models")
            {
                return JsonResponse("""
                    {
                      "data": [
                        {"id":"deepinfra-video-model","owned_by":"deepinfra"},
                        {"id":"black-forest-labs/FLUX.1-schnell","owned_by":"deepinfra"}
                      ]
                    }
                    """);
            }

            return Unexpected(request);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Contains(models, model => model.Name == "deepinfra-video-model" && model.Type == "video");
        Assert.Contains(models, model => model.Name == "black-forest-labs/FLUX.1-schnell" && model.Type == "image");
    }

    private static DeepInfraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new DeepInfraProvider(new StaticApiKeyResolver(), httpClientFactory, cache);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpResponseMessage Unexpected(HttpRequestMessage request)
        => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected request: {request.Method} {request.RequestUri}")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
