using System.Net;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ElevenLabs;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ElevenLabs;

public sealed class ElevenLabsProviderModelsTests
{
    [Fact]
    public async Task Model_discovery_includes_current_scribe_models()
    {
        var provider = CreateProvider();

        var models = (await provider.ListModels()).ToDictionary(model => model.Name);

        Assert.Equal("transcription", models["scribe_v2_medical"].Type);

        foreach (var modelId in new[]
                 {
                     "scribe_v2_realtime",
                     "scribe_v2_realtime_turbo",
                     "scribe_v2_realtime_lite"
                 })
        {
            Assert.Equal("transcription", models[modelId].Type);
            Assert.Contains("real-time", models[modelId].Tags ?? []);
        }
    }

    private static ElevenLabsProvider CreateProvider()
    {
        var client = new HttpClient(new Handler())
        {
            BaseAddress = new Uri("https://api.elevenlabs.io/")
        };

        return new ElevenLabsProvider(
            new ApiKeyResolver(),
            new HttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    }

    private sealed class ApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });
    }
}
