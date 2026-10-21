using System.Net;
using System.Net.Mime;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Alibaba;

public sealed class AlibabaProviderMediaDownloadTests
{
    [Theory]
    [InlineData("qwen-image-plus")]
    [InlineData("wan2.6-t2i")]
    public async Task Image_generation_downloads_output_without_forwarding_provider_credentials(string model)
    {
        string? apiAuthorization = null;
        string? mediaAuthorization = "unset";
        var imageBytes = "generated image"u8.ToArray();
        var provider = CreateProvider((clientName, request) =>
        {
            if (clientName == "alibaba-media")
            {
                mediaAuthorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(imageBytes) };
            }

            apiAuthorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://cdn.example/image.png"}]}}]}}
                    """,
                    MediaTypeNames.Application.Json)
            };
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = model,
            Prompt = "A mountain at sunrise"
        });

        Assert.StartsWith("Bearer ", apiAuthorization);
        Assert.Null(mediaAuthorization);
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}",
            Assert.Single(response.Images!));
    }

    private static AlibabaProvider CreateProvider(
        Func<string, HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(
        Func<string, HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RecordingHandler(name, responder));
    }

    private sealed class RecordingHandler(
        string clientName,
        Func<string, HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(clientName, request));
        }
    }
}
