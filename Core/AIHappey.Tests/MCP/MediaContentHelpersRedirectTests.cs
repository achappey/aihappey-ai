using System.Net;
using AIHappey.Core.MCP.Media;

namespace AIHappey.Tests.MCP;

public sealed class MediaContentHelpersRedirectTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    public async Task FetchProviderImageOutputAsBase64Async_rejects_mapped_ipv6_private_addresses(string address)
    {
        var requests = new List<Uri>();
        var factory = new TestHttpClientFactory(request =>
        {
            requests.Add(request.RequestUri!);
            return ImageResponse();
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaContentHelpers.FetchProviderImageOutputAsBase64Async(
                new Uri($"http://[::ffff:{address}]/image.png"),
                factory,
                CancellationToken.None));

        Assert.Contains("private/loopback", exception.Message);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task FetchProviderImageOutputAsBase64Async_rejects_redirects_to_private_addresses()
    {
        var requests = new List<Uri>();
        var factory = new TestHttpClientFactory(request =>
        {
            requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://127.0.0.1/latest/image.png");
            return response;
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaContentHelpers.FetchProviderImageOutputAsBase64Async(
                new Uri("https://192.0.2.1/image.png"),
                factory,
                CancellationToken.None));

        Assert.Contains("private/loopback", exception.Message);
        Assert.Equal([new Uri("https://192.0.2.1/image.png")], requests);
    }

    [Fact]
    public async Task FetchProviderImageOutputAsBase64Async_follows_public_redirects()
    {
        var requests = new List<Uri>();
        var factory = new TestHttpClientFactory(request =>
        {
            requests.Add(request.RequestUri!);
            if (requests.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://198.51.100.1/image.png");
                return redirect;
            }

            return ImageResponse();
        });

        var (mimeType, base64) = await MediaContentHelpers.FetchProviderImageOutputAsBase64Async(
            new Uri("https://192.0.2.1/start.png"),
            factory,
            CancellationToken.None);

        Assert.Equal("image/png", mimeType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), base64);
        Assert.Equal(
            [new Uri("https://192.0.2.1/start.png"), new Uri("https://198.51.100.1/image.png")],
            requests);
    }

    private static HttpResponseMessage ImageResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    }

    private sealed class TestHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("mcp-provider-image-output", name);
            return new HttpClient(new RecordingHandler(responder));
        }
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
