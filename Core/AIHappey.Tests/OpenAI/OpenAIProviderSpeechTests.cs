using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequestPostsRestPayloadAndReturnsBinaryAudioWithMetadataCost()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/audio/speech")
            {
                capturedRequest = CloneRequest(request);
                return BinaryResponse([1, 2, 3], "audio/wav");
            }

            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                return JsonResponse("""
                    {
                      "data": [
                        {
                          "id": "tts-1",
                          "object": "model",
                          "created": 1710000000,
                          "owned_by": "openai"
                        }
                      ]
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "hello",
            Voice = "nova",
            OutputFormat = "wav",
            Speed = 1.25f,
            Instructions = "speak warmly"
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/audio/speech", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", capturedRequest.Headers.Authorization?.Parameter);

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("tts-1", root.GetProperty("model").GetString());
        Assert.Equal("hello", root.GetProperty("input").GetString());
        Assert.Equal("nova", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.Equal(1.25, root.GetProperty("speed").GetDouble(), 3);
        Assert.Equal("speak warmly", root.GetProperty("instructions").GetString());

        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.NotNull(response.Request?.Body);

    }

    [Fact]
    public async Task SpeechRequestUsesProviderOptionsAndFallsBackMimeTypeWhenResponseOmitsContentType()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/audio/speech")
            {
                capturedRequest = CloneRequest(request);
                return BinaryResponse([4, 5, 6], contentType: null);
            }

            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                return JsonResponse("""
                    {
                      "data": [
                        {
                          "id": "tts-1",
                          "object": "model",
                          "created": 1710000000,
                          "owned_by": "openai"
                        }
                      ]
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "metadata driven",
            ProviderOptions = ProviderOptions(new OpenAiSpeechProviderMetadata
            {
                Voice = "sage",
                ResponseFormat = "pcm",
                Speed = 0.75f,
                Instructions = "calm"
            })
        });

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("sage", root.GetProperty("voice").GetString());
        Assert.Equal("pcm", root.GetProperty("response_format").GetString());
        Assert.Equal(0.75, root.GetProperty("speed").GetDouble(), 3);
        Assert.Equal("calm", root.GetProperty("instructions").GetString());
        Assert.Equal("audio/pcm", response.Audio.MimeType);
        Assert.Equal("pcm", response.Audio.Format);
    }

    [Fact]
    public async Task SpeechRequestThrowsOpenAiErrorBodyOnFailure()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/audio/speech", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"bad input\"}}", Encoding.UTF8, "application/json")
            };
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "bad input"
        }));

        Assert.Contains("OpenAI speech request failed (400)", ex.Message);
        Assert.Contains("bad input", ex.Message);
    }

    private static OpenAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new NullEndUserIdResolver());

    private static Dictionary<string, JsonElement> ProviderOptions(OpenAiSpeechProviderMetadata metadata)
        => new()
        {
            ["openai"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        if (request.Headers.Authorization is not null)
            clone.Headers.Authorization = request.Headers.Authorization;

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private static HttpResponseMessage BinaryResponse(byte[] body, string? contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            response.Content.Headers.ContentType = new(contentType);

        return response;
    }

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
