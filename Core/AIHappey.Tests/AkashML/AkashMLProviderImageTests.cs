using System.Net;
using System.Net.Mime;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AkashML;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AkashML;

public sealed class AkashMLProviderImageTests
{
    [Fact]
    public async Task Image_generation_downloads_output_without_forwarding_provider_credentials()
    {
        string? apiAuthorization = null;
        string? mediaAuthorization = "unset";
        var imageBytes = "generated image"u8.ToArray();
        var provider = CreateProvider((name, request) =>
        {
            if (name == "akashml-media")
            {
                mediaAuthorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(imageBytes) };
            }

            apiAuthorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"url":"https://cdn.example/image.png"}]}""",
                    Encoding.UTF8,
                    MediaTypeNames.Application.Json)
            };
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "image-model",
            Prompt = "A mountain at sunrise"
        });

        Assert.Equal("******", apiAuthorization);
        Assert.Null(mediaAuthorization);
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}",
            Assert.Single(response.Images!));
    }

    private static AkashMLProvider CreateProvider(
        Func<string, HttpRequestMessage, HttpResponseMessage> responder)
    {
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new AkashMLProvider(new StaticApiKeyResolver(), cache, new TestHttpClientFactory(responder));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(
        Func<string, HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RecordingHandler(request => responder(name, request)));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
