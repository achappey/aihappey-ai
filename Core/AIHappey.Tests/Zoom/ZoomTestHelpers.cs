using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Zoom;

namespace AIHappey.Tests.Zoom;

internal static class ZoomTestHelpers
{
    internal static ZoomProvider Provider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string? key = "zoom-secret")
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(send))));

    internal static ZoomProvider NoHttp()
        => Provider((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP request."));

    internal static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider)
        {
            Assert.Equal("zoom", provider);
            return key;
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
