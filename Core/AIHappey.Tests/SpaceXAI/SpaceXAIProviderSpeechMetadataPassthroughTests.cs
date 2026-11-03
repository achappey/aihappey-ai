using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.SpaceXAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SpaceXAI;

public sealed class SpaceXAIProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_merges_unknown_provider_options_at_upstream_root_and_preserves_known_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                capturedRequest = CloneRequest(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };
            }

            return JsonResponse(request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => """{"data":[]}""",
                "/v1/tts/voices" => """{"voices":[]}""",
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            });
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "spacexai/tts",
            Text = "hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["spacexai"] = JsonDocument.Parse(
                    """
                    {
                      "language": "en",
                      "voice_id": "Alex",
                      "output_format": {
                        "codec": "wave",
                        "sample_rate": 24000,
                        "bit_rate": null,
                        "channel_layout": "mono",
                        "future_format": { "channels": [1, null, 2] }
                      },
                      "future_config": {
                        "values": [1, null, { "enabled": true }],
                        "explicit_null": null
                      },
                      "future_count": 3,
                      "future_enabled": true,
                      "future_name": "native",
                      "text": "must not replace request text"
                    }
                    """).RootElement.Clone()
            }
        });

        using var document = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var payload = document.RootElement;
        Assert.Equal("hello", payload.GetProperty("text").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal("alex", payload.GetProperty("voice_id").GetString());
        Assert.Equal("wav", payload.GetProperty("output_format").GetProperty("codec").GetString());
        Assert.Equal(24000, payload.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
        Assert.False(payload.GetProperty("output_format").TryGetProperty("bit_rate", out _));
        Assert.Equal("mono", payload.GetProperty("output_format").GetProperty("channel_layout").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("output_format").GetProperty("future_format")[1].ValueKind);
        Assert.Equal(1, payload.GetProperty("future_config").GetProperty("values")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_config").GetProperty("values")[1].ValueKind);
        Assert.True(payload.GetProperty("future_config").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_config").GetProperty("explicit_null").ValueKind);
        Assert.Equal(3, payload.GetProperty("future_count").GetInt32());
        Assert.True(payload.GetProperty("future_enabled").GetBoolean());
        Assert.Equal("native", payload.GetProperty("future_name").GetString());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("spacexai", out _));
        Assert.Equal("wav", response.Audio!.Format);
    }

    [Fact]
    public async Task Speech_request_keeps_defaults_and_omits_empty_output_format()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                capturedRequest = CloneRequest(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1])
                };
            }

            return JsonResponse(request.RequestUri!.AbsolutePath == "/v1/models"
                ? """{"data":[]}"""
                : """{"voices":[]}""");
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "spacexai/tts",
            Text = "hello"
        });

        using var document = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        Assert.Equal("auto", document.RootElement.GetProperty("language").GetString());
        Assert.Equal("eve", document.RootElement.GetProperty("voice_id").GetString());
        Assert.False(document.RootElement.TryGetProperty("output_format", out _));
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_request_preserves_codec_validation_and_upstream_error_behavior()
    {
        var invalidCodecProvider = CreateProvider(_ =>
            throw new InvalidOperationException("Invalid codec must not be sent upstream."));
        await Assert.ThrowsAsync<ArgumentException>(() => invalidCodecProvider.SpeechRequest(new SpeechRequest
        {
            Model = "spacexai/tts",
            Text = "hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["spacexai"] = JsonSerializer.SerializeToElement(new
                {
                    output_format = new { codec = "unsupported" }
                })
            }
        }));

        var errorProvider = CreateProvider(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("upstream error")
            }
            : JsonResponse(request.RequestUri!.AbsolutePath == "/v1/models"
                ? """{"data":[]}"""
                : """{"voices":[]}"""));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            errorProvider.SpeechRequest(new SpeechRequest
            {
                Model = "spacexai/tts",
                Text = "hello"
            }));
        Assert.Contains("TTS failed (400): upstream error", exception.Message);
    }

    private static SpaceXAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StaticResponseHandler(responder))
        {
            BaseAddress = new Uri("https://api.x.ai/")
        };
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new SpaceXAIProvider(new StaticApiKeyResolver(), cache, new StaticHttpClientFactory(client));
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
            clone.Content = new StringContent(request.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Encoding.UTF8, "application/json");
        return clone;
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
