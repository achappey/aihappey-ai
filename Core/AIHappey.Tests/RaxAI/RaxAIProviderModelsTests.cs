using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.RaxAI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.RaxAI;

public sealed class RaxAIProviderModelsTests
{
    [Fact]
    public async Task ListModels_reads_max_output_when_it_is_an_integer()
    {
        var provider = CreateProvider("""{"data":[{"id":"rax-model","max_output":4096}]}""");

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal(4096, model.MaxTokens);
    }

    [Theory]
    [InlineData("\"4096\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("2147483648")]
    public async Task ListModels_ignores_max_output_when_it_is_not_a_supported_integer(string maxOutput)
    {
        var provider = CreateProvider($$"""{"data":[{"id":"rax-model","max_output":{{maxOutput}}}]}""");

        var model = Assert.Single(await provider.ListModels());

        Assert.Null(model.MaxTokens);
    }

    private static RaxAIProvider CreateProvider(string json)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(json))));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
