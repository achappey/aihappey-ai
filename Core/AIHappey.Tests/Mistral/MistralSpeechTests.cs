using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Mistral;

public sealed class MistralSpeechTests
{
    [Fact]
    public async Task Speech_request_model_voice_shortcut_resolves_voice_slug_to_voice_id()
    {
        HttpRequestMessage? capturedSpeechRequest = null;

        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.PathAndQuery == "/v1/audio/voices?limit=100&offset=0")
            {
                return JsonResponse("""
                    {
                      "items": [
                        { "id": "voice_123", "name": "Ava", "slug": "ava" }
                      ],
                      "page": 1,
                      "page_size": 100,
                      "total": 1,
                      "total_pages": 1
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath == "/v1/audio/speech")
            {
                capturedSpeechRequest = CloneRequest(request);
                return JsonResponse("""
                    {
                      "audio_data": "YmFzZTY0LWF1ZGlv"
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "mistral-tts-latest/ava",
            Text = "hello world",
            Voice = "should-be-ignored",
            OutputFormat = "wav"
        });

        Assert.NotNull(capturedSpeechRequest);
        var body = await capturedSpeechRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("mistral-tts-latest", root.GetProperty("model").GetString());
        Assert.Equal("hello world", root.GetProperty("input").GetString());
        Assert.Equal("voice_123", root.GetProperty("voice_id").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());

        Assert.Equal("YmFzZTY0LWF1ZGlv", response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Contains(response.Warnings, warning => warning.ToString()!.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task List_models_marks_tts_models_as_speech_and_adds_voice_shortcuts()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                return JsonResponse("""
                    {
                      "data": [
                        {
                          "id": "mistral-small-latest",
                          "object": "model",
                          "created": 1735689600,
                          "owned_by": "mistral"
                        },
                        {
                          "id": "mistral-tts-latest",
                          "object": "model",
                          "created": 1735689601,
                          "owned_by": "mistral"
                        }
                      ]
                    }
                    """);
            }

            if (request.RequestUri?.PathAndQuery == "/v1/audio/voices?limit=100&offset=0")
            {
                return JsonResponse("""
                    {
                      "items": [
                        { "id": "voice_123", "name": "Ava", "slug": "ava" },
                        { "id": "voice_456", "name": "Luna", "slug": "luna" }
                      ],
                      "page": 1,
                      "page_size": 100,
                      "total": 2,
                      "total_pages": 1
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath == "/v1/agents")
                return JsonResponse("[]");

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Contains(models, model => model.Name == "mistral-tts-latest" && model.Type == "speech");
        Assert.Contains(models, model => model.Name == "mistral-tts-latest/ava" && model.Type == "speech");
        Assert.Contains(models, model => model.Name == "mistral-tts-latest/luna" && model.Type == "speech");
    }

    private static MistralProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.mistral.ai/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new MistralProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
