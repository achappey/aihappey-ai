using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Featherless;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Featherless;

public sealed class FeatherlessProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequest_sends_bulk_binary_payload_with_raw_provider_options()
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/audio/speech", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());

            capturedPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            var response = AudioResponse([1, 2, 3], "audio/x-wav");
            response.Headers.Add("X-Generation-Id", "generation-1");
            response.Headers.Add("X-Input-Characters", "20");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "hexgrad/Kokoro-82M",
            Text = "Hello from Featherless.",
            Voice = "af_bella",
            OutputFormat = "wav",
            Speed = 1.25f,
            Instructions = "Speak warmly.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["featherless"] = Options("""
                {
                  "voice": "provider-voice",
                  "speed": 2,
                  "delivery": "bulk",
                  "encoding": "binary",
                  "exaggeration": 0.6
                }
                """)
            }
        });

        Assert.NotNull(capturedPayload);
        Assert.Equal("hexgrad/Kokoro-82M", capturedPayload.Value.GetProperty("model").GetString());
        Assert.Equal("Hello from Featherless.", capturedPayload.Value.GetProperty("input").GetString());
        Assert.Equal("af_bella", capturedPayload.Value.GetProperty("voice").GetString());
        Assert.Equal("wav", capturedPayload.Value.GetProperty("response_format").GetString());
        Assert.Equal(1.25f, capturedPayload.Value.GetProperty("speed").GetSingle());
        Assert.Equal("Speak warmly.", capturedPayload.Value.GetProperty("instructions").GetString());
        Assert.Equal("bulk", capturedPayload.Value.GetProperty("delivery").GetString());
        Assert.Equal("binary", capturedPayload.Value.GetProperty("encoding").GetString());
        Assert.Equal(0.6, capturedPayload.Value.GetProperty("exaggeration").GetDouble());

        Assert.Equal("AQID", response.Audio.Base64);
        Assert.Equal("audio/x-wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Equal("generation-1", response.Response.Headers["X-Generation-Id"]);
        Assert.Equal("featherless/hexgrad/Kokoro-82M", response.Response.ModelId);
    }

    [Fact]
    public async Task SpeechRequest_defaults_format_delivery_and_encoding_without_requiring_voice()
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(async request =>
        {
            capturedPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return AudioResponse([4], "audio/mpeg");
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "hexgrad/Kokoro-82M",
            Text = "Hello"
        });

        Assert.NotNull(capturedPayload);
        Assert.False(capturedPayload.Value.TryGetProperty("voice", out _));
        Assert.Equal("mp3", capturedPayload.Value.GetProperty("response_format").GetString());
        Assert.Equal("bulk", capturedPayload.Value.GetProperty("delivery").GetString());
        Assert.Equal("binary", capturedPayload.Value.GetProperty("encoding").GetString());
        Assert.Equal("mp3", response.Audio.Format);
        Assert.Equal("BA==", response.Audio.Base64);
    }

    [Fact]
    public async Task SpeechRequest_decodes_json_base64_passthrough_response()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"object":"audio.speech","format":"flac","audio":"CQg="}""", Encoding.UTF8, "application/json")
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "speech-model",
            Text = "Hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["featherless"] = Options("""{"delivery":"json","encoding":"base64"}""")
            }
        });

        Assert.Equal("CQg=", response.Audio.Base64);
        Assert.Equal("flac", response.Audio.Format);
        Assert.Equal("audio/flac", response.Audio.MimeType);
    }

    [Fact]
    public async Task SpeechRequest_validates_required_fields_before_sending()
    {
        var provider = CreateProvider((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("No request expected.")));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = " ",
            Text = "Hello"
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "speech-model",
            Text = " "
        }));
    }

    [Fact]
    public async Task SpeechRequest_includes_status_and_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"error":{"code":"invalid_voice"}}""", Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "speech-model",
            Text = "Hello",
            Voice = "missing"
        }));

        Assert.Contains("422", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_voice", exception.Message, StringComparison.Ordinal);
    }

    private static FeatherlessProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static FeatherlessProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.featherless.ai/")
        };

        return new FeatherlessProvider(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(httpClient),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    }

    private static HttpResponseMessage AudioResponse(byte[] audio, string contentType)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(audio)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
            }
        };

    private static JsonElement Options(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "featherless" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
