using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.HuggingFace;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.HuggingFace;

public sealed class HuggingFaceProviderModelsTests
{
    [Fact]
    public async Task ListModels_sends_bearer_authentication()
    {
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            Assert.Equal("/v1/models", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"meta-llama/Llama-3.1-8B-Instruct","owned_by":"meta-llama"}]}""", Encoding.UTF8, "application/json")
            };
        });
        var provider = new HuggingFaceProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(handler)));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("huggingface/meta-llama/Llama-3.1-8B-Instruct", model.Id);
        Assert.Equal("meta-llama", model.OwnedBy);
    }

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
