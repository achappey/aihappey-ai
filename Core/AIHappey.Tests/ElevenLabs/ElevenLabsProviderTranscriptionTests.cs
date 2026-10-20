using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ElevenLabs;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ElevenLabs;

public sealed class ElevenLabsProviderTranscriptionTests
{
    [Theory]
    [InlineData("", "scribe_v2")]
    [InlineData("scribe_v1", "scribe_v1")]
    public async Task Transcription_request_uses_scribe_v2_by_default_and_preserves_explicit_model(
        string requestedModel,
        string expectedModel)
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"transcribed"}""", Encoding.UTF8, "application/json")
            };
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = requestedModel,
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav"
        });

        Assert.Equal("transcribed", response.Text);
        Assert.Contains(expectedModel, requestBody);
    }

    private static ElevenLabsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new ApiKeyResolver(), new HttpClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private sealed class ApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
