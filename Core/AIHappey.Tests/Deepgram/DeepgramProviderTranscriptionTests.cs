using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Deepgram;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Deepgram;

public sealed class DeepgramProviderTranscriptionTests
{
    [Fact]
    public async Task Transcription_request_returns_word_segments_when_requested()
    {
        string? requestQuery = null;
        var provider = CreateProvider(request =>
        {
            requestQuery = request.RequestUri?.Query;
            return JsonResponse("""
                {
                  "results": {
                    "channels": [{
                      "alternatives": [{
                        "transcript": "Hello, world",
                        "words": [
                          { "word": "hello", "punctuated_word": "Hello,", "start": 0.1, "end": 0.4 },
                          { "word": "world", "start": 0.5, "end": 0.9 }
                        ]
                      }]
                    }]
                  },
                  "metadata": { "duration": 1.0 }
                }
                """);
        });

        var response = await provider.TranscriptionRequest(CreateRequest("word"));

        Assert.DoesNotContain("timestamp_granularities", requestQuery);
        Assert.DoesNotContain("utterances", requestQuery);
        Assert.Collection(
            response.Segments,
            segment =>
            {
                Assert.Equal("Hello,", segment.Text);
                Assert.Equal(0.1f, segment.StartSecond);
                Assert.Equal(0.4f, segment.EndSecond);
            },
            segment =>
            {
                Assert.Equal("world", segment.Text);
                Assert.Equal(0.5f, segment.StartSecond);
                Assert.Equal(0.9f, segment.EndSecond);
            });
    }

    [Fact]
    public async Task Transcription_request_uses_utterances_for_segment_timestamps()
    {
        string? requestQuery = null;
        var provider = CreateProvider(request =>
        {
            requestQuery = request.RequestUri?.Query;
            return JsonResponse("""
                {
                  "results": {
                    "channels": [{
                      "alternatives": [{ "transcript": "Hello, world" }]
                    }],
                    "utterances": [
                      { "transcript": "Hello, world", "start": 0.1, "end": 0.9 }
                    ]
                  }
                }
                """);
        });

        var response = await provider.TranscriptionRequest(CreateRequest("segment"));

        Assert.Contains("utterances=true", requestQuery);
        var segment = Assert.Single(response.Segments);
        Assert.Equal("Hello, world", segment.Text);
        Assert.Equal(0.1f, segment.StartSecond);
        Assert.Equal(0.9f, segment.EndSecond);
    }

    private static TranscriptionRequest CreateRequest(string granularity)
        => new()
        {
            Model = "nova-3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["deepgram"] = JsonSerializer.SerializeToElement(new
                {
                    timestamp_granularities = new[] { granularity }
                })
            }
        };

    private static DeepgramProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new ApiKeyResolver(), new HttpClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

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
