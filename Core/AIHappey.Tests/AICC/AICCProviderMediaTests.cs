using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AICC;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AICC;

public sealed class AICCProviderMediaTests
{
    [Fact]
    public async Task Image_edit_rejects_remote_input_urls_without_http_requests()
    {
        var requests = new List<Uri>();
        var provider = CreateProvider((_, request) =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "openai/image-model",
            Prompt = "Edit this image",
            Files = [new ImageFile { Data = "https://input.example/image.png", MediaType = MediaTypeNames.Image.Png }]
        }));

        Assert.Contains("Remote image URLs are not supported", exception.Message);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Image_generation_downloads_output_without_forwarding_provider_credentials()
    {
        string? apiAuthorization = null;
        string? mediaAuthorization = "unset";
        var imageBytes = "generated image"u8.ToArray();
        var provider = CreateProvider((name, request) =>
        {
            if (name == "aicc-media")
            {
                mediaAuthorization = request.Headers.Authorization?.ToString();
                var content = new ByteArrayContent(imageBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Image.Png);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
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
            Model = "openai/image-model",
            Prompt = "A mountain at sunrise"
        });

        Assert.StartsWith("Bearer ", apiAuthorization);
        Assert.Null(mediaAuthorization);
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}",
            Assert.Single(response.Images!));
    }

    [Fact]
    public async Task Video_generation_downloads_output_without_forwarding_provider_credentials()
    {
        var apiAuthorizations = new List<string?>();
        string? mediaAuthorization = "unset";
        var videoBytes = "generated video"u8.ToArray();
        var provider = CreateProvider((name, request) =>
        {
            if (name == "aicc-media")
            {
                mediaAuthorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(videoBytes) };
            }

            apiAuthorizations.Add(request.Headers.Authorization?.ToString());
            var body = request.RequestUri!.AbsolutePath.EndsWith("/v1/video/generations", StringComparison.Ordinal)
                ? """{"id":"task-1"}"""
                : """{"status":"succeeded","url":"https://cdn.example/video.mp4"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
            };
        });

        var response = await provider.VideoRequest(new VideoRequest
        {
            Model = "video-model",
            Prompt = "A mountain at sunrise"
        });

        Assert.NotEmpty(apiAuthorizations);
        Assert.All(apiAuthorizations, authorization => Assert.StartsWith("Bearer ", authorization));
        Assert.Null(mediaAuthorization);
        Assert.Equal(Convert.ToBase64String(videoBytes), Assert.Single(response.Videos!).Data);
    }

    private static AICCProvider CreateProvider(
        Func<string, HttpRequestMessage, HttpResponseMessage> responder)
    {
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new AICCProvider(new StaticApiKeyResolver(), cache, new TestHttpClientFactory(responder));
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
