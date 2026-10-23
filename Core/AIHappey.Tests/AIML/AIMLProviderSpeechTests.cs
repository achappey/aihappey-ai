using System.Net;
using System.Net.Http.Headers;
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
        Assert.False(root.TryGetProperty("speed", out _));
        Assert.False(root.TryGetProperty("instructions", out _));

        Assert.Equal(Convert.ToBase64String(audioBytes), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        var warnings = response.Warnings.Select(warning => JsonSerializer.SerializeToElement(warning)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal("unsupported", warning.GetProperty("type").GetString()));
        Assert.Contains(warnings, warning => warning.GetProperty("feature").GetString() == "instructions");
        Assert.Contains(warnings, warning => warning.GetProperty("feature").GetString() == "speed");
        Assert.NotNull(response.Request);

        var returnedRequestBody = JsonSerializer.SerializeToElement(response.Request!.Body, JsonSerializerOptions.Web);
        Assert.Equal("openai/gpt-4o-mini-tts", returnedRequestBody.GetProperty("model").GetString());
        Assert.Equal("hello world", returnedRequestBody.GetProperty("text").GetString());
        Assert.Equal("coral", returnedRequestBody.GetProperty("voice").GetString());
        Assert.Equal("mp3", returnedRequestBody.GetProperty("response_format").GetString());
        Assert.False(returnedRequestBody.TryGetProperty("speed", out _));
        Assert.False(returnedRequestBody.TryGetProperty("instructions", out _));
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

    [Theory]
    [InlineData("audio/wav", "audio/wav", "wav")]
    [InlineData("audio/mpeg", "audio/mpeg", "mp3")]
    [InlineData("audio/wav; charset=binary", "audio/wav", "wav")]
    [InlineData("AUDIO/MPEG", "AUDIO/MPEG", "mp3")]
    public async Task SpeechRequest_returns_binary_audio_based_on_content_type(string contentType, string expectedMimeType, string expectedFormat)
    {
        var audioBytes = new byte[] { 10, 20, 30, 0, 255 };
        var requestCount = 0;
        var provider = CreateProvider(request =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/tts", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioBytes)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            response.Headers.Add("X-AIML-Request-Id", "binary-request");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "elevenlabs/eleven_multilingual_v2",
            Text = "hello world",
            // The response Content-Type, not the requested format, is authoritative.
            OutputFormat = "flac",
            ProviderOptions = new() { ["aiml"] = JsonSerializer.SerializeToElement(new { stream = true }) }
        }, CancellationToken.None);

        Assert.Equal(1, requestCount);
        Assert.Equal(Convert.ToBase64String(audioBytes), response.Audio.Base64);
        Assert.Equal(audioBytes, Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal(expectedMimeType, response.Audio.MimeType);
        Assert.Equal(expectedFormat, response.Audio.Format);
        Assert.Empty(response.Warnings);
        Assert.NotNull(response.Request);
        var returnedRequest = JsonSerializer.SerializeToElement(response.Request.Body, JsonSerializerOptions.Web);
        Assert.Equal("elevenlabs/eleven_multilingual_v2", returnedRequest.GetProperty("model").GetString());
        Assert.Equal("hello world", returnedRequest.GetProperty("text").GetString());
        Assert.Equal("flac", returnedRequest.GetProperty("format").GetString());
        Assert.True(returnedRequest.GetProperty("stream").GetBoolean());
        Assert.NotNull(response.Response);
        Assert.Equal("binary-request", response.Response.Headers!["X-AIML-Request-Id"]);
        Assert.Equal(contentType, response.Response.Headers["Content-Type"]);
        Assert.Null(response.Response.Body);
        Assert.NotNull(response.ProviderMetadata);
        Assert.Equal(JsonValueKind.Object, response.ProviderMetadata["aiml"].ValueKind);
        Assert.Empty(response.ProviderMetadata["aiml"].EnumerateObject());
        Assert.False(response.ProviderMetadata.ContainsKey("gateway"));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    public async Task SpeechRequest_downloads_json_audio_url_and_preserves_metadata(string contentType)
    {
        var audioBytes = new byte[] { 1, 2, 3, 4 };
        var requestedUris = new List<string>();
        var provider = CreateProvider(request =>
        {
            requestedUris.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath == "/v1/tts")
            {
                var response = JsonResponse("""
                    {"audio":{"url":"https://cdn.example.test/generated/audio.wav"},
                     "meta":{"usage":{"usd_spent":0.001,"characters":11},"metrics":{"latency_ms":42}}}
                    """);
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                return response;
            }

            Assert.Equal("https://cdn.example.test/generated/audio.wav", request.RequestUri.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            var audioResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audioBytes) };
            audioResponse.Content.Headers.ContentType = new("audio/wav");
            audioResponse.Headers.Add("X-Audio-Request-Id", "download-request");
            return audioResponse;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "openai/gpt-4o-mini-tts",
            Text = "hello world",
            Voice = "coral",
            OutputFormat = "mp3"
        }, CancellationToken.None);

        Assert.Equal(2, requestedUris.Count);
        Assert.Equal(Convert.ToBase64String(audioBytes), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Empty(response.Warnings);
        Assert.NotNull(response.Request);
        var returnedRequest = JsonSerializer.SerializeToElement(response.Request.Body, JsonSerializerOptions.Web);
        Assert.Equal("openai/gpt-4o-mini-tts", returnedRequest.GetProperty("model").GetString());
        Assert.Equal("hello world", returnedRequest.GetProperty("text").GetString());
        Assert.Equal("coral", returnedRequest.GetProperty("voice").GetString());
        Assert.Equal("mp3", returnedRequest.GetProperty("format").GetString());
        Assert.Equal("download-request", response.Response.Headers!["X-Audio-Request-Id"]);
        var responseBody = Assert.IsType<JsonElement>(response.Response.Body);
        Assert.Equal("https://cdn.example.test/generated/audio.wav", responseBody.GetProperty("audio").GetProperty("url").GetString());
        Assert.NotNull(response.ProviderMetadata);
        Assert.Equal(0.001m, response.ProviderMetadata["gateway"].GetProperty("cost").GetDecimal());
        Assert.Equal(11, response.ProviderMetadata["aiml"].GetProperty("usage").GetProperty("characters").GetInt32());
        Assert.Equal(42, response.ProviderMetadata["aiml"].GetProperty("metrics").GetProperty("latency_ms").GetInt32());
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("audio/wav")]
    [InlineData("audio/mpeg")]
    public async Task SpeechRequest_preserves_error_handling_for_json_and_binary_content_types(string contentType)
    {
        var requestCount = 0;
        var provider = CreateProvider(_ =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("provider failure", Encoding.UTF8)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return response;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "openai/gpt-4o-mini-tts",
            Text = "hello world"
        }, CancellationToken.None));

        Assert.Contains("AIML TTS failed (400): provider failure", error.Message);
        Assert.Equal(1, requestCount);
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
