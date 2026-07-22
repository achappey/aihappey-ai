using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cortecs;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cortecs;

public sealed class CortecsProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequest_uses_request_voice_and_preserves_raw_provider_options()
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/audio/speech", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());

            capturedPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return AudioResponse([1, 2, 3], "audio/mpeg");
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "cortecs/chatterbox-turbo",
            Text = "Hello from Cortecs.",
            Voice = "request-voice",
            OutputFormat = "wav",
            Speed = 1.25f,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cortecs"] = Options("""
                {
                  "voice": "metadata-voice",
                  "preference": "speed",
                  "allowed_providers": ["tensorix"]
                }
                """)
            }
        });

        Assert.NotNull(capturedPayload);
        Assert.Equal("cortecs/chatterbox-turbo", capturedPayload.Value.GetProperty("model").GetString());
        Assert.Equal("Hello from Cortecs.", capturedPayload.Value.GetProperty("input").GetString());
        Assert.Equal("request-voice", capturedPayload.Value.GetProperty("voice").GetString());
        Assert.Equal("wav", capturedPayload.Value.GetProperty("response_format").GetString());
        Assert.Equal(1.25f, capturedPayload.Value.GetProperty("speed").GetSingle());
        Assert.Equal("speed", capturedPayload.Value.GetProperty("preference").GetString());
        Assert.Equal("tensorix", capturedPayload.Value.GetProperty("allowed_providers")[0].GetString());

        Assert.Equal("wav", response.Audio.Format);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("AQID", response.Audio.Base64);
    }

    [Fact]
    public async Task SpeechRequest_uses_provider_metadata_voice_when_request_voice_is_missing()
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(async request =>
        {
            capturedPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return AudioResponse([4]);
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "cortecs/chatterbox-turbo",
            Text = "Hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cortecs"] = Options("""{ "voice": "metadata-voice" }""")
            }
        });

        Assert.NotNull(capturedPayload);
        Assert.Equal("metadata-voice", capturedPayload.Value.GetProperty("voice").GetString());
    }

    [Fact]
    public async Task SpeechRequest_throws_when_voice_is_not_supplied()
    {
        var provider = CreateProvider((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("No HTTP request should be sent.")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "cortecs/chatterbox-turbo",
            Text = "Hello"
        }));

        Assert.Contains("Voice is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeechRequest_includes_cortecs_beta_endpoint_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("beta endpoint unavailable", Encoding.UTF8, "text/plain")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "cortecs/chatterbox-turbo",
            Text = "Hello",
            Voice = "voice"
        }));

        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("beta endpoint unavailable", exception.Message, StringComparison.Ordinal);
    }

    private static CortecsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static CortecsProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.cortecs.ai/")
        };

        return new CortecsProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

    private static HttpResponseMessage AudioResponse(byte[] audio, string contentType = "audio/wav")
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
        public string? Resolve(string provider) => provider == "cortecs" ? "test-key" : null;
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
