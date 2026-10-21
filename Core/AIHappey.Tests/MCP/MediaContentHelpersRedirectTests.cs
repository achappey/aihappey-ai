using System.Net;
using AIHappey.Core.MCP.Media;

namespace AIHappey.Tests.MCP;

public sealed class MediaContentHelpersRedirectTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    public async Task FetchExternalBodyAsTextAsync_rejects_mapped_ipv6_private_addresses(string address)
    {
        var requests = new List<Uri>();
        var factory = new TestHttpClientFactory(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("unexpected") };
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaContentHelpers.FetchExternalBodyAsTextAsync(
                new Uri($"http://[::ffff:{address}]/document"),
                factory,
                CancellationToken.None));

        Assert.Contains("private/loopback", exception.Message);
        Assert.Empty(requests);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    public async Task FetchExternalBodyAsTextAsync_rejects_redirects_to_mapped_ipv6_private_addresses(string address)
    {
        var requests = new List<Uri>();
        var factory = new TestHttpClientFactory(request =>
        {
            requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri($"http://[::ffff:{address}]/document");
            return response;
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaContentHelpers.FetchExternalBodyAsTextAsync(
                new Uri("https://192.0.2.1/start"),
                factory,
                CancellationToken.None));

        Assert.Contains("private/loopback", exception.Message);
        Assert.Equal([new Uri("https://192.0.2.1/start")], requests);
    }

    [Fact]
    public async Task FetchExternalBodyAsTextAsync_rejects_redirects_to_private_addresses()
    {
        var requests = new List<Uri>();
        var factory = new TestHttpClientFactory(request =>
        {
            requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://127.0.0.1/latest/meta-data");
            return response;
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaContentHelpers.FetchExternalBodyAsTextAsync(
                new Uri("https://192.0.2.1/document"),
                factory,
                CancellationToken.None));

        Assert.Contains("private/loopback", exception.Message);
        Assert.Equal([new Uri("https://192.0.2.1/document")], requests);
    }

    [Fact]
    public async Task FetchExternalBodyAsTextAsync_follows_public_redirects()
    {
        var requests = new List<Uri>();
        var factory = new TestHttpClientFactory(request =>
        {
            requests.Add(request.RequestUri!);
            if (requests.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://198.51.100.1/document");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("document body") };
        });

        var text = await MediaContentHelpers.FetchExternalBodyAsTextAsync(
            new Uri("https://192.0.2.1/start"),
            factory,
            CancellationToken.None);

        Assert.Equal("document body", text);
        Assert.Equal(
            [new Uri("https://192.0.2.1/start"), new Uri("https://198.51.100.1/document")],
            requests);
    }

    private sealed class TestHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("mcp-public-url-fetch", name);
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
