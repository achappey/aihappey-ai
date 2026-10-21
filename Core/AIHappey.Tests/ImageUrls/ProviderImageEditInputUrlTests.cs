using System.Net;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AIsa;
using AIHappey.Core.Providers.NanoGPT;
using AIHappey.Core.Providers.PayPerQ;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ImageUrls;

public sealed class ProviderImageEditInputUrlTests
{
    [Theory]
    [InlineData("nanogpt", false)]
    [InlineData("nanogpt", true)]
    [InlineData("payperq", false)]
    [InlineData("payperq", true)]
    [InlineData("aisa", false)]
    [InlineData("aisa", true)]
    public async Task OpenAIImageEditRequestAsync_rejects_remote_inputs_without_http_requests(string providerId, bool useMask)
    {
        var requests = new List<Uri>();
        var handler = new RecordingHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"b64_json":"aW1hZ2U="}]}""")
            };
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheHelper = new AsyncCacheHelper(cache);
        var factory = new TestHttpClientFactory(handler);

        Func<OpenAIImageEditRequest, Task<OpenAIImagesResponse>> edit = providerId switch
        {
            "nanogpt" => request => new NanoGPTProvider(new StaticApiKeyResolver(), cacheHelper, factory)
                .OpenAIImageEditRequestAsync(request),
            "payperq" => request => new PayPerQProvider(new StaticApiKeyResolver(), cacheHelper, factory)
                .OpenAIImageEditRequestAsync(request),
            "aisa" => request => new AIsaProvider(new StaticApiKeyResolver(), cacheHelper, factory)
                .OpenAIImageEditRequestAsync(request),
            _ => throw new ArgumentOutOfRangeException(nameof(providerId))
        };

        var remoteImage = new OpenAIImageReference { ImageUrl = "https://input.example/image.png" };
        var request = new OpenAIImageEditRequest
        {
            Model = "test-model",
            Prompt = "edit image",
            Images = useMask ? [new OpenAIImageReference { ImageUrl = "data:image/png;base64,aW1hZ2U=" }] : [remoteImage],
            Mask = useMask ? remoteImage : null
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => edit(request));

        Assert.Contains("Remote image URLs are not supported", exception.Message);
        Assert.Empty(requests);
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
