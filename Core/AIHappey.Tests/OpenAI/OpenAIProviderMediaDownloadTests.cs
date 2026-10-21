using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIProviderMediaDownloadTests
{
    [Fact]
    public async Task WebSearchImageDownload_does_not_send_openai_api_key_to_media_host()
    {
        AuthenticationHeaderValue? mediaAuthorization = null;
        using var handler = new RecordingHandler(request =>
        {
            mediaAuthorization = request.Headers.Authorization;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("image"u8.ToArray())
            };
        });
        var provider = new OpenAIProvider(
            new StaticApiKeyResolver(),
            new TestHttpClientFactory(handler),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticEndUserIdResolver());
        var authenticatedClient = (HttpClient)typeof(OpenAIProvider)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider)!;
        authenticatedClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "provider-secret");
        var download = typeof(OpenAIProvider).GetMethod(
            "TryDownloadImageBytesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)download.Invoke(
            provider,
            ["https://media.example/image.png", CancellationToken.None])!;

        Assert.Equal("Bearer", authenticatedClient.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Null(mediaAuthorization);
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "provider-secret";
    }

    private sealed class StaticEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.openai.com/")
            };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
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
