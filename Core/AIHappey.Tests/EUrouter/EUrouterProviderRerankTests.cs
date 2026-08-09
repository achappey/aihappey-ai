using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.EUrouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.EUrouter;

public sealed class EUrouterProviderRerankTests
{
    [Fact]
    public async Task RerankingRequest_posts_payload_and_maps_response_metadata()
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
                Content = JsonResponse("""
                {
                  "id": "rerank_123",
                  "model": "qwen/qwen3-reranker-4b",
                  "results": [
                    {"index": 0, "relevance_score": 0.31, "document": {"text": "A general document."}},
                    {"index": 1, "relevance_score": 0.93, "document": {"text": "European AI infrastructure."}}
                  ],
                  "usage": {
                    "prompt_tokens": 22,
                    "total_tokens": 22,
                    "search_units": 1,
                    "cost": 0.00042,
                    "cost_currency": "EUR",
                    "cost_eur": 0.00042,
                    "is_byok": false
                  },
                  "provider": "sample-provider"
                }
                """)
            };
            response.Headers.Add("X-Request-Id", "request_123");
            return response;
        });

        var result = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "qwen/qwen3-reranker-4b",
            Query = "European sovereign AI",
            TopN = 1,
            Documents = TextDocuments("A general document.", "European AI infrastructure."),
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["eurouter"] = JsonSerializer.SerializeToElement(new
                {
                    provider = new
                    {
                        order = new[] { "sample-provider" },
                        allow_fallbacks = false,
                        eu_owned = true
                    },
                    trace = new { trace_id = "trace-123", span_name = "rerank" },
                    session_id = "session-123"
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);
        var root = payload.RootElement;

        Assert.Equal("/api/v1/rerank", requestedPath);
        Assert.Equal("Bearer test-key", authorization);
        Assert.Equal("qwen/qwen3-reranker-4b", root.GetProperty("model").GetString());
        Assert.Equal("European sovereign AI", root.GetProperty("query").GetString());
        Assert.Equal(2, root.GetProperty("documents").GetArrayLength());
        Assert.Equal(1, root.GetProperty("top_n").GetInt32());
        Assert.False(root.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(root.GetProperty("provider").GetProperty("eu_owned").GetBoolean());
        Assert.Equal("trace-123", root.GetProperty("trace").GetProperty("trace_id").GetString());
        Assert.Equal("session-123", root.GetProperty("session_id").GetString());

        var ranking = Assert.Single(result.Ranking);
        Assert.Equal(1, ranking.Index);
        Assert.Equal(0.93f, ranking.RelevanceScore);
        Assert.Equal("rerank_123", result.Response.Id);
        Assert.Equal("eurouter/qwen/qwen3-reranker-4b", result.Response.ModelId);
        Assert.Equal("request_123", result.Response.Headers["X-Request-Id"]);
        Assert.Empty(result.Warnings);

        var body = Assert.IsType<JsonElement>(result.Response.Body);
        Assert.Equal("sample-provider", body.GetProperty("provider").GetString());
        Assert.Equal(1, result.ProviderMetadata!["eurouter"]
            .GetProperty("usage").GetProperty("search_units").GetInt32());
        Assert.Equal(0.00042m, result.ProviderMetadata["gateway"].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task RerankingRequest_allows_provider_options_to_override_standard_fields()
    {
        var requestJson = string.Empty;
        var provider = CreateProvider(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonResponse("""{"model":"override-model","results":[],"usage":{}}""")
            };
        });

        await provider.RerankingRequest(new RerankingRequest
        {
            Model = "original-model",
            Query = "original-query",
            TopN = 1,
            Documents = TextDocuments("original-document"),
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["eurouter"] = JsonSerializer.SerializeToElement(new
                {
                    model = "override-model",
                    query = "override-query",
                    documents = new[] { "override-document" },
                    top_n = 2
                })
            }
        });

        using var payload = JsonDocument.Parse(requestJson);
        Assert.Equal("override-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("override-query", payload.RootElement.GetProperty("query").GetString());
        Assert.Equal("override-document", payload.RootElement.GetProperty("documents")[0].GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("top_n").GetInt32());
    }

    [Fact]
    public async Task RerankingRequest_adds_warning_when_results_are_missing()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonResponse("""{"model":"qwen/qwen3-reranker-4b","usage":{}}""")
        });

        var result = await provider.RerankingRequest(CreateRequest());

        Assert.Empty(result.Ranking);
        var warning = JsonSerializer.SerializeToElement(Assert.Single(result.Warnings));
        Assert.Equal("provider_response_missing_field", warning.GetProperty("type").GetString());
        Assert.Equal("results", warning.GetProperty("feature").GetString());
    }

    [Fact]
    public async Task RerankingRequest_throws_descriptive_error_for_unsuccessful_response()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid rerank request")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RerankingRequest(CreateRequest()));

        Assert.Contains("EUrouter rerank request failed (400): invalid rerank request", exception.Message);
    }

    [Fact]
    public async Task RerankingRequest_rejects_empty_documents_before_sending()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP request should not be sent."));
        var request = CreateRequest();
        request.Documents = TextDocuments();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.RerankingRequest(request));

        Assert.Contains("At least one document is required", exception.Message);
    }

    private static RerankingRequest CreateRequest()
        => new()
        {
            Model = "qwen/qwen3-reranker-4b",
            Query = "European sovereign AI",
            Documents = TextDocuments("A document.")
        };

    private static RerankingDocument TextDocuments(params string[] values)
        => new()
        {
            Type = "text",
            Values = JsonSerializer.SerializeToElement(values, JsonSerializerOptions.Web)
        };

    private static StringContent JsonResponse(string json)
        => new(json, Encoding.UTF8, MediaTypeNames.Application.Json);

    private static EUrouterProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new EUrouterProvider(
            new StaticApiKeyResolver(),
            cache,
            new StaticHttpClientFactory(client));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
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
            => Task.FromResult(responder(request));
    }
}
