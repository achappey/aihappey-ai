using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cohere;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cohere;

public sealed class CohereProviderModelsTests
{
    [Fact]
    public async Task ListModels_follows_next_page_token()
    {
        var requests = new List<Uri?>();
        var provider = CreateProvider(request =>
        {
            requests.Add(request.RequestUri);
            return requests.Count switch
            {
                1 => JsonResponse("""
                    {
                      "models": [
                        { "name": "command-r-08-2024", "context_length": 128000 }
                      ],
                      "next_page_token": "second-page"
                    }
                    """),
                2 => JsonResponse("""
                    {
                      "models": [
                        { "name": "embed-english-v3.0", "features": ["embed"] }
                      ]
                    }
                    """),
                _ => throw new InvalidOperationException("Unexpected model-list request.")
            };
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal(["cohere/command-r-08-2024", "cohere/embed-english-v3.0"], models.Select(model => model.Id));
        Assert.Equal(["language", "embedding"], models.Select(model => model.Type));
        Assert.Equal(2, requests.Count);
        Assert.Contains("page_size=1000", requests[0]!.Query);
        Assert.Contains("page_size=1000", requests[1]!.Query);
        Assert.Contains("page_token=second-page", requests[1]!.Query);
    }

    private static CohereProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))
            {
                BaseAddress = new Uri("https://api.cohere.com/")
            }));

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
