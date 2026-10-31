using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Groq;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Groq;

public sealed class GroqProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_passes_unknown_provider_options_at_the_root_and_preserves_overrides()
    {
        string? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return SuccessResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "playai-tts",
            Text = "Hello from Groq",
            Voice = "request-voice",
            OutputFormat = "mp3",
            Speed = 1.25f,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["groq"] = JsonSerializer.SerializeToElement(new
                {
                    voice = "metadata-voice",
                    response_format = "flac",
                    speed = 0.5,
                    custom_config = new
                    {
                        values = new object?[] { 1, null, new { enabled = true } },
                        explicit_null = (string?)null
                    }
                })
            }
        });

        using var payload = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        var root = payload.RootElement;
        Assert.Equal("playai-tts", root.GetProperty("model").GetString());
        Assert.Equal("Hello from Groq", root.GetProperty("input").GetString());
        Assert.Equal("request-voice", root.GetProperty("voice").GetString());
        Assert.Equal("mp3", root.GetProperty("response_format").GetString());
        Assert.Equal(1.25, root.GetProperty("speed").GetDouble());
        Assert.Equal(1, root.GetProperty("custom_config").GetProperty("values")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("custom_config").GetProperty("values")[1].ValueKind);
        Assert.True(root.GetProperty("custom_config").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("custom_config").GetProperty("explicit_null").ValueKind);
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("groq", out _));
    }

    [Fact]
    public async Task Speech_request_keeps_default_voice_and_format_without_provider_options()
    {
        string? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return SuccessResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "playai-tts",
            Text = "Hello from Groq"
        });

        using var payload = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        var root = payload.RootElement;
        Assert.Equal("troy", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.False(root.TryGetProperty("speed", out _));
        Assert.Equal(4, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_request_keeps_invalid_known_metadata_error_behavior()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "playai-tts",
            Text = "Hello from Groq",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["groq"] = JsonSerializer.SerializeToElement(new { voice = 42 })
            }
        }));
    }

    [Fact]
    public async Task Speech_request_keeps_upstream_error_details()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SpeechRequest(new SpeechRequest
            {
                Model = "playai-tts",
                Text = "Hello from Groq"
            }));

        Assert.Contains("Groq TTS failed (400): upstream failure", exception.Message);
    }

    private static GroqProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new CapturingHandler(responder));
        var factory = new StaticHttpClientFactory(client);
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new GroqProvider(new StaticApiKeyResolver(), cache, factory);
    }

    private static HttpResponseMessage SuccessResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("generated audio"))
        };

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
            => responder(request);
    }
}
