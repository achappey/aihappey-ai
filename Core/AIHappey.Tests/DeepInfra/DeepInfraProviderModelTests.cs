using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepInfra;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.DeepInfra;

public sealed class DeepInfraProviderModelTests
{
    [Fact]
    public async Task ListModels_merges_deduplicates_classifies_and_caches_both_catalogs()
    {
        var calls = new List<string>();
        var provider = CreateProvider(request =>
        {
            Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            return path switch
            {
                "/v1/models" => JsonResponse("""{"data":[{"id":"org/chat","owned_by":"org"},{"id":"org/decision","owned_by":"org"},{"id":"org/chat","owned_by":"org"}]}"""),
                "/typesafe/v1/models" => JsonResponse("""{"models":[{"name":"org/decision","description":"Decisions","release_date":"2026-01-02"},{"name":"other-decision","description":"Other","release_date":"unknown"}]}"""),
                _ => throw new InvalidOperationException("Unexpected URL")
            };
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(3, models.Length);
        Assert.Contains(models, model => model.Id == "deepinfra/org/chat" && model.OwnedBy == "org");
        var decision = Assert.Single(models, model => model.Id == "deepinfra/org/decision");
        Assert.Equal("decision", decision.Type);
        Assert.Equal("Decisions", decision.Description);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), decision.Created);
        Assert.Null(Assert.Single(models, model => model.Name == "other-decision").Created);
        Assert.Equal(3, (await provider.ListModels()).Count());
        Assert.Equal(new[] { "/v1/models", "/typesafe/v1/models" }, calls);
    }

    [Theory]
    [InlineData("/v1/models")]
    [InlineData("/typesafe/v1/models")]
    public async Task ListModels_does_not_cache_a_partial_snapshot_after_catalog_failure(string failedPath)
    {
        var fail = true;
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            var path = request.RequestUri!.AbsolutePath;
            if (fail && path == failedPath)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("catalog unavailable") };
            return path == "/v1/models" ? JsonResponse("""{"data":[]}""") : JsonResponse("""{"models":[]}""");
        });
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.ListModels());
        Assert.Contains("catalog unavailable", exception.Message);
        var failedCalls = calls;
        fail = false;
        Assert.Empty(await provider.ListModels());
        Assert.Equal(failedCalls + 2, calls);
    }

    private static DeepInfraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
