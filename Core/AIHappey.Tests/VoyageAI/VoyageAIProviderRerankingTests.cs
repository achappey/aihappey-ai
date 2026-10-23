using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.VoyageAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.VoyageAI;

public sealed class VoyageAIProviderRerankingTests
{
    [Fact]
    public async Task Reranking_request_uses_official_top_k_parameter()
    {
        string? requestPath = null;
        string? requestBody = null;
        string? authorization = null;

        var provider = new VoyageAIProvider(
            new TestApiKeyResolver(),
            new TestHttpClientFactory(new HttpClient(new TestHttpMessageHandler(request =>
            {
                requestPath = request.RequestUri?.AbsolutePath;
                requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                authorization = request.Headers.Authorization?.ToString();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"index":0,"relevance_score":0.9}]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }))));

        var result = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "rerank-2.5",
            Query = "query",
            TopN = 1,
            Documents = new RerankingDocument
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[] { "document" })
            }
        });

        using var payload = JsonDocument.Parse(requestBody!);

        Assert.Equal("/v1/rerank", requestPath);
        Assert.StartsWith("Bearer ", authorization);
        Assert.Equal(1, payload.RootElement.GetProperty("top_k").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("top_n", out _));
        Assert.Equal(0, Assert.Single(result.Ranking).Index);
    }

    private sealed class TestApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
