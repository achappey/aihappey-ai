using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Soniox;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Soniox;

public sealed class SonioxProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_passes_soniox_options_to_the_upstream_root_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("/tts", request.RequestUri!.AbsolutePath);
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return AudioResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "soniox/tts-v1/voice-1",
            Voice = "request-voice",
            Text = "Hello",
            Language = "en",
            OutputFormat = "wave",
            Speed = 1.5f,
            ProviderOptions = new()
            {
                ["soniox"] = ParseElement("""
                    {
                      "sample_rate": 24000,
                      "bitrate": 128000,
                      "client_reference_id": "client-123",
                      "reduce_silence": true,
                      "model": "wrong-model",
                      "voice": "wrong-voice",
                      "text": "wrong-text",
                      "language": "ja",
                      "audio_format": "mp3",
                      "speed": 0.5,
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        });

        Assert.Equal("tts-v1", payload.GetProperty("model").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voice").GetString());
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal("wav", payload.GetProperty("audio_format").GetString());
        Assert.Equal(1.5, payload.GetProperty("speed").GetDouble());
        Assert.Equal(24000, payload.GetProperty("sample_rate").GetInt32());
        Assert.Equal(128000, payload.GetProperty("bitrate").GetInt32());
        Assert.Equal("client-123", payload.GetProperty("client_reference_id").GetString());
        Assert.True(payload.GetProperty("reduce_silence").GetBoolean());

        var expectedUnknown = ParseElement("""
            {
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        foreach (var property in expectedUnknown.EnumerateObject())
        {
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("soniox", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(16, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_request_keeps_defaults_omission_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        var provider = CreateProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return AudioResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-v1/voice-1",
            Text = "Hello",
            Language = "en",
            ProviderOptions = new()
            {
                ["soniox"] = ParseElement("""{"sample_rate":null,"future_null":null}""")
            }
        });

        Assert.Equal("mp3", payload.GetProperty("audio_format").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("sample_rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("bitrate").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("client_reference_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("reduce_silence").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("speed").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(11, payload.EnumerateObject().Count());

        var invalidProvider = CreateProvider(_ =>
            throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-v1/voice-1",
            Text = "Hello",
            Language = "en",
            ProviderOptions = new()
            {
                ["soniox"] = ParseElement("""{"sample_rate":"high"}""")
            }
        }));
        var missingVoice = await Assert.ThrowsAsync<ArgumentException>(() => invalidProvider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-v1",
            Text = "Hello",
            Language = "en"
        }));
        Assert.Contains("Voice is required", missingVoice.Message);

        var missingLanguage = await Assert.ThrowsAsync<ArgumentException>(() => invalidProvider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-v1/voice-1",
            Text = "Hello"
        }));
        Assert.Contains("Language is required", missingLanguage.Message);

        var failedProvider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failedProvider.SpeechRequest(new SpeechRequest
            {
                Model = "tts-v1/voice-1",
                Text = "Hello",
                Language = "en"
            }));
        Assert.Contains("Soniox TTS failed (400): upstream failure", error.Message);
    }

    private static SonioxProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new CapturingHandler(responder))));

    private static HttpResponseMessage AudioResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
        };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
