using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cohere;

public sealed class CohereRerankingTests
{
    [Fact]
    public async Task Reranking_request_succeeds_when_optional_model_lookup_fails()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v2/rerank")
                return JsonResponse("""
                    {
                      "id": "rerank-1",
                      "results": [
                        { "index": 0, "relevance_score": 0.9 }
                      ],
                      "meta": {
                        "billed_units": { "search_units": 1 }
                      }
                    }
                    """);

            if (request.RequestUri?.AbsolutePath == "/v1/models")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        var response = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "rerank-v4.0-pro",
            Query = "query",
            Documents = new RerankingDocument
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[] { "document" })
            }
        });

        var ranking = Assert.Single(response.Ranking);
        Assert.Equal(0, ranking.Index);
        Assert.Equal(0.9f, ranking.RelevanceScore);
        Assert.Equal("rerank-1", response.Response.Id);
    }

    [Fact]
    public async Task Reranking_request_propagates_cancellation_during_optional_model_lookup()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var provider = CreateProvider(new CancelOnModelLookupHttpMessageHandler(cancellationTokenSource));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RerankingRequest(new RerankingRequest
        {
            Model = "rerank-v4.0-pro",
            Query = "query",
            Documents = new RerankingDocument
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[] { "document" })
            }
        }, cancellationTokenSource.Token));
    }

    private static CohereProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(new StaticResponseHttpMessageHandler(responder));

    private static CohereProvider CreateProvider(HttpMessageHandler handler)
    {
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cohere.com/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new CohereProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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

    private sealed class CancelOnModelLookupHttpMessageHandler(CancellationTokenSource cancellationTokenSource) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/v2/rerank")
                return Task.FromResult(JsonResponse("""
                    {
                      "id": "rerank-1",
                      "results": [
                        { "index": 0, "relevance_score": 0.9 }
                      ],
                      "meta": {
                        "billed_units": { "search_units": 1 }
                      }
                    }
                    """));

            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                cancellationTokenSource.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellationTokenSource.Token);
            }

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        }
    }
}
