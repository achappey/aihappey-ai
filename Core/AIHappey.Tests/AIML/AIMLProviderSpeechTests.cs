using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AIML;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AIML;

public sealed class AIMLProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequest_posts_to_v1_tts_and_downloads_json_audio_url()
    {
        HttpRequestMessage? capturedTtsRequest = null;
        var audioBytes = new byte[] { 1, 2, 3, 4 };

        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/tts")
            {
                capturedTtsRequest = CloneRequest(request);
                return JsonResponse("""
                    {
                      "audio": {
                        "url": "https://cdn.example.test/generated/audio.wav"
                      },
                      "usage": {
                        "characters": 11
                      }
                    }
                    """);
            }

            if (request.RequestUri?.AbsoluteUri == "https://cdn.example.test/generated/audio.wav")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(audioBytes)
                    {
                        Headers =
                        {
                            ContentType = new("audio/wav")
                        }
                    }
                };
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        });

        var providerOptions = JsonSerializer.SerializeToElement(new
        {
            model = "raw-model",
            text = "raw text",
            voice = "raw-voice",
            response_format = "wav",
            seed = 42,
            voice_settings = new
            {
                stability = 0.5
            }
        }, JsonSerializerOptions.Web);

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "openai/gpt-4o-mini-tts",
            Text = "hello world",
            Voice = "coral",
            OutputFormat = "mp3",
            Speed = 1.1f,
            Instructions = "speak warmly",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["aiml"] = providerOptions
            }
        }, CancellationToken.None);

        Assert.NotNull(capturedTtsRequest);
        Assert.Equal(HttpMethod.Post, capturedTtsRequest!.Method);
        Assert.Equal("/v1/tts", capturedTtsRequest.RequestUri?.AbsolutePath);

        var body = await capturedTtsRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("openai/gpt-4o-mini-tts", root.GetProperty("model").GetString());
        Assert.Equal("hello world", root.GetProperty("text").GetString());
        Assert.Equal("coral", root.GetProperty("voice").GetString());
        Assert.Equal("mp3", root.GetProperty("response_format").GetString());
        Assert.Equal(42, root.GetProperty("seed").GetInt32());
        Assert.Equal(0.5, root.GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.Equal(1.1f, root.GetProperty("speed").GetSingle());
        Assert.False(root.TryGetProperty("instructions", out _));

        Assert.Equal(Convert.ToBase64String(audioBytes), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Contains(response.Warnings, warning => warning.ToString()!.Contains("instructions", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(response.Request);

        var returnedRequestBody = JsonSerializer.SerializeToElement(response.Request!.Body, JsonSerializerOptions.Web);
        Assert.Equal("openai/gpt-4o-mini-tts", returnedRequestBody.GetProperty("model").GetString());
        Assert.Equal("hello world", returnedRequestBody.GetProperty("text").GetString());
        Assert.Equal("coral", returnedRequestBody.GetProperty("voice").GetString());
        Assert.Equal("mp3", returnedRequestBody.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task SpeechRequest_returns_direct_binary_audio_response()
    {
        HttpRequestMessage? capturedTtsRequest = null;
        var audioBytes = new byte[] { 10, 20, 30 };

        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/tts")
            {
                capturedTtsRequest = CloneRequest(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(audioBytes)
                    {
                        Headers =
                        {
                            ContentType = new("audio/mpeg")
                        }
                    }
                };
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        });

        var providerOptions = JsonSerializer.SerializeToElement(new
        {
            stream = true,
            output_format = "mp3_44100_128"
        }, JsonSerializerOptions.Web);

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "elevenlabs/eleven_multilingual_v2",
            Text = "hello world",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["aiml"] = providerOptions
            }
        }, CancellationToken.None);

        Assert.NotNull(capturedTtsRequest);
        var body = await capturedTtsRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("elevenlabs/eleven_multilingual_v2", root.GetProperty("model").GetString());
        Assert.Equal("hello world", root.GetProperty("text").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("mp3_44100_128", root.GetProperty("output_format").GetString());

        Assert.Equal(Convert.ToBase64String(audioBytes), response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("mp3", response.Audio.Format);
    }

    private static AIMLProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new AIMLProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
