using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AIsa;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AIsa;

public sealed class AIsaProviderImageTests
{
    [Fact]
    public async Task ImageRequest_downloads_generated_image_without_forwarding_provider_credentials()
    {
        var imageBytes = "generated image"u8.ToArray();
        string? apiAuthorization = null;
        string? imageAuthorization = "unset";
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.Host == "api.aisa.one")
            {
                apiAuthorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"created":1,"data":[{"url":"https://cdn.example/image.png"}]}""")
                };
            }

            imageAuthorization = request.Headers.Authorization?.ToString();
            var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Image.Png);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "aisa/image-model",
            Prompt = "A mountain at sunrise"
        });

        Assert.StartsWith("Bearer ", apiAuthorization);
        Assert.Null(imageAuthorization);
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}",
            Assert.Single(response.Images!));
    }

    private static AIsaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticResponseHandler(responder));
    }

    private sealed class StaticResponseHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
