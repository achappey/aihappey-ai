using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SovrGPT;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SovrGPT;

public sealed class SovrGPTProviderRerankTests
{
    [Fact]
    public async Task RerankingRequest_posts_cohere_compatible_payload_and_preserves_provider_response()
    {
        var requestedPath = string.Empty;
        var authorization = string.Empty;
        var requestJson = string.Empty;

        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery ?? string.Empty;
            authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "id": "rerank_123",
                  "model": "bge-reranker-v2-m3",
                  "results": [
                    {"index": 1, "relevance_score": 0.93},
                    {"index": 0, "relevance_score": 0.31}
                  ],
                  "meta": {"billed_units": {"search_units": 2}}
                }
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            };
            response.Headers.Add("X-Request-Id", "request_123");
            return response;
        });

        var result = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "bge-reranker-v2-m3",
            Query = "European sovereign AI",
            TopN = 1,
            Documents = new RerankingDocument
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[]
                {
                    "A general document.",
                    "SovrGPT provides European AI infrastructure."
                }, JsonSerializerOptions.Web)
            },
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["sovrgpt"] = JsonSerializer.SerializeToElement(new
                {
                    return_documents = true,
                    model = "ignored-model",
                    query = "ignored-query",
                    documents = new[] { "ignored-document" },
                    top_n = 99
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("/api/v1/rerank", requestedPath);
        Assert.Equal("Bearer test-key", authorization);
        Assert.Equal("bge-reranker-v2-m3", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("European sovereign AI", payload.RootElement.GetProperty("query").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("top_n").GetInt32());
        Assert.True(payload.RootElement.GetProperty("return_documents").GetBoolean());
        Assert.Equal(2, payload.RootElement.GetProperty("documents").GetArrayLength());

        var rankings = result.Ranking.ToArray();
        Assert.Equal([1, 0], rankings.Select(ranking => ranking.Index).ToArray());
        Assert.Equal(0.93f, rankings[0].RelevanceScore);
        Assert.Equal("sovrgpt/bge-reranker-v2-m3", result.Response.ModelId);
        Assert.Equal("rerank_123", result.Response.Id);
        Assert.Equal("request_123", result.Response.Headers["X-Request-Id"]);
        var responseBody = Assert.IsType<JsonElement>(result.Response.Body);
        Assert.Equal(2, responseBody.GetProperty("meta").GetProperty("billed_units").GetProperty("search_units").GetInt32());
        Assert.Equal(2, result.ProviderMetadata!["sovrgpt"].GetProperty("meta").GetProperty("billed_units").GetProperty("search_units").GetInt32());
    }

    [Fact]
    public async Task RerankingRequest_throws_descriptive_error_for_unsuccessful_response()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid rerank request")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RerankingRequest(CreateRequest()));

        Assert.Contains("SovrGPT rerank request failed (400): invalid rerank request", exception.Message);
    }

    private static RerankingRequest CreateRequest()
        => new()
        {
            Model = "bge-reranker-v2-m3",
            Query = "European sovereign AI",
            Documents = new RerankingDocument
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[] { "A document." }, JsonSerializerOptions.Web)
            }
        };

    private static SovrGPTProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new SovrGPTProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

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
