using System.Net;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Deepgram;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Deepgram;

public sealed class DeepgramProviderSpeechTests
{
    [Fact]
    public async Task Speech_request_forwards_speed_for_aura_2()
    {
        Uri? requestUri = null;
        var provider = CreateProvider(request =>
        {
            requestUri = request.RequestUri;
            return AudioResponse();
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-2-thalia-en",
            Text = "Hello",
            Speed = 1.15f
        });

        Assert.Contains("speed=1.15", requestUri!.Query);
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task Speech_request_keeps_speed_unsupported_warning_for_non_aura_2_models()
    {
        Uri? requestUri = null;
        var provider = CreateProvider(request =>
        {
            requestUri = request.RequestUri;
            return AudioResponse();
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-asteria-en",
            Text = "Hello",
            Speed = 1.15f
        });

        Assert.DoesNotContain("speed=", requestUri!.Query);
        Assert.Single(response.Warnings);
    }

    [Theory]
    [InlineData(0.69f)]
    [InlineData(1.51f)]
    [InlineData(1.13f)]
    public async Task Speech_request_rejects_unsupported_aura_2_speeds(float speed)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-2-thalia-en",
            Text = "Hello",
            Speed = speed
        }));
    }

    private static DeepgramProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage AudioResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
            {
                Headers = { ContentType = new("audio/mpeg") }
            }
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
