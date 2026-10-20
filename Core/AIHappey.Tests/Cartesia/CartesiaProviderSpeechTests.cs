using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cartesia;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cartesia;

public sealed class CartesiaProviderSpeechTests
{
    [Theory]
    [InlineData("pcm", "raw", "audio/pcm")]
    [InlineData("ogg", "wav", "audio/wav")]
    public async Task SpeechRequest_reports_the_format_sent_to_Cartesia(
        string outputFormat,
        string expectedContainer,
        string contentType)
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/tts/bytes", request.RequestUri?.AbsolutePath);
            capturedPayload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new(contentType);
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Text = "Hello from Cartesia.",
            OutputFormat = outputFormat
        });

        Assert.NotNull(capturedPayload);
        Assert.Equal(expectedContainer, capturedPayload.Value
            .GetProperty("output_format").GetProperty("container").GetString());
        Assert.Equal(expectedContainer, response.Audio.Format);
        Assert.Equal(contentType, response.Audio.MimeType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
    }

    private static CartesiaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new CartesiaProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

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
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
