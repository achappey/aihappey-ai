using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.LumaAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.LumaAI;

public sealed class LumaAIProviderVideoTests
{
    [Fact]
    public async Task GetVideoOperationStatus_authenticates_poll_but_not_presigned_download()
    {
        const string operation = "generation-1";
        const string videoUrl = "https://luma-results.example/video.mp4?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Signature=test-signature";
        byte[] expectedVideo = [0, 1, 2, 3, 4];
        AuthenticationHeaderValue? pollAuthorization = null;
        AuthenticationHeaderValue? downloadAuthorization = null;

        var apiClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/v1/generations/{operation}", request.RequestUri?.AbsolutePath);
            pollAuthorization = request.Headers.Authorization;

            return JsonResponse(new
            {
                id = operation,
                model = "ray-3.2",
                state = "completed",
                output = new[] { new { type = "video", url = videoUrl } }
            });
        }));

        var downloadClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(videoUrl, request.RequestUri?.AbsoluteUri);
            downloadAuthorization = request.Headers.Authorization;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedVideo)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") }
                }
            };
        }));

        var provider = new LumaAIProvider(
            new StaticApiKeyResolver(),
            new SequencedHttpClientFactory(apiClient, downloadClient));

        var result = await provider.GetVideoOperationStatus(operation);

        Assert.Equal("Bearer", pollAuthorization?.Scheme);
        Assert.Equal("test-api-key", pollAuthorization?.Parameter);
        Assert.Null(downloadAuthorization);

        var completed = Assert.IsType<VideoOperationCompletedResult>(result);
        var video = Assert.Single(completed.Videos);
        Assert.Equal("base64", video.Type);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(Convert.ToBase64String(expectedVideo), Assert.IsType<string>(video.Data));
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class SequencedHttpClientFactory(params HttpClient[] clients) : IHttpClientFactory
    {
        private int _index;

        public HttpClient CreateClient(string name)
        {
            Assert.True(_index < clients.Length, "The provider requested more HTTP clients than expected.");
            return clients[_index++];
        }
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
