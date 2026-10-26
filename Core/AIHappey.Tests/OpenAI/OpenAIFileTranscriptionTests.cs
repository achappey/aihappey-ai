using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIFileTranscriptionTests
{
    [Fact]
    public async Task Transcription_preserves_diarized_segments_in_provider_metadata()
    {
        var provider = CreateProvider(_ => JsonResponse("""
            {
              "text": "Hello there.",
              "duration": 2.5,
              "segments": [
                { "text": "Hello there.", "start": 0.2, "end": 1.4, "speaker": "speaker_1" }
              ]
            }
            """));

        var result = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "gpt-4o-transcribe-diarize",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["openai"] = JsonSerializer.SerializeToElement(new { response_format = "diarized_json" })
            }
        });

        var segment = Assert.Single(result.Segments);
        Assert.Equal("Hello there.", segment.Text);
        Assert.Equal(0.2f, segment.StartSecond);
        Assert.Equal(1.4f, segment.EndSecond);
        Assert.Equal(
            "speaker_1",
            result.ProviderMetadata!["openai"].GetProperty("segments")[0].GetProperty("speaker").GetString());
    }

    private static OpenAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new NullEndUserIdResolver());

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class NullEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
