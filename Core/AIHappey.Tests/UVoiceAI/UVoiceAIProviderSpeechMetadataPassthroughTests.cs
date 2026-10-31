using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.UVoiceAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.UVoiceAI;

public sealed class UVoiceAIProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task SpeechRequest_preserves_unknown_provider_options_inside_api_settings()
    {
        string? upstreamBody = null;
        using var metadata = JsonDocument.Parse("""
            {
              "outputFormat": "mp3",
              "outputType": "binary",
              "autoBreak": false,
              "volume": 1.2,
              "pitch": null,
              "key": -2,
              "speed": 0.8,
              "voiceID": "metadata-voice",
              "text": "metadata text",
              "vendor_extension": {
                "nested": [1, true, null, {"leaf": "preserved"}]
              },
              "future_number": 12,
              "explicit_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "uvoiceai/voice-123",
            Text = "A sufficiently long test sentence.",
            OutputFormat = "wave",
            Speed = 1.1f,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["uvoiceai"] = metadata.RootElement.Clone(),
                ["another-provider"] = JsonDocument.Parse("""{"must_not_leak":true}""").RootElement.Clone()
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        var provider = CreateProvider(message =>
        {
            upstreamBody = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return AudioResponse();
        });

        await provider.SpeechRequest(request);

        Assert.True(serializedClientRequest.GetProperty("providerOptions").GetProperty("uvoiceai")
            .TryGetProperty("vendor_extension", out _));
        using var body = JsonDocument.Parse(Assert.IsType<string>(upstreamBody));
        var root = body.RootElement;
        Assert.Equal(1, root.EnumerateObject().Count());
        var settings = root.GetProperty("settings");
        Assert.Equal("voice-123", settings.GetProperty("voiceID").GetString());
        Assert.Equal("A sufficiently long test sentence.", settings.GetProperty("text").GetString());
        Assert.Equal("wav", settings.GetProperty("outputFormat").GetString());
        Assert.Equal("binary", settings.GetProperty("outputType").GetString());
        Assert.False(settings.GetProperty("autoBreak").GetBoolean());
        Assert.Equal(1.2f, settings.GetProperty("volume").GetSingle());
        Assert.Equal(-2, settings.GetProperty("key").GetInt32());
        Assert.Equal(1.1f, settings.GetProperty("speed").GetSingle());
        Assert.False(settings.TryGetProperty("pitch", out _));
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"nested":[1,true,null,{"leaf":"preserved"}]}""").RootElement,
            settings.GetProperty("vendor_extension")));
        Assert.Equal(12, settings.GetProperty("future_number").GetInt32());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("explicit_null").ValueKind);
        Assert.False(settings.TryGetProperty("another-provider", out _));
        Assert.False(settings.TryGetProperty("providerOptions", out _));
    }

    [Fact]
    public async Task SpeechRequest_keeps_defaults_and_omits_null_mapped_options()
    {
        string? upstreamBody = null;
        using var metadata = JsonDocument.Parse("""
            {
              "autoBreak": null,
              "volume": null,
              "pitch": null,
              "key": null,
              "future_null": null
            }
            """);
        var provider = CreateProvider(message =>
        {
            upstreamBody = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return AudioResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "uvoiceai/voice-123",
            Text = "A sufficiently long test sentence.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["uvoiceai"] = metadata.RootElement.Clone()
            }
        });

        using var body = JsonDocument.Parse(Assert.IsType<string>(upstreamBody));
        var settings = body.RootElement.GetProperty("settings");
        Assert.Equal("wav", settings.GetProperty("outputFormat").GetString());
        Assert.Equal("binary", settings.GetProperty("outputType").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("future_null").ValueKind);
        Assert.False(settings.TryGetProperty("autoBreak", out _));
        Assert.False(settings.TryGetProperty("volume", out _));
        Assert.False(settings.TryGetProperty("pitch", out _));
        Assert.False(settings.TryGetProperty("key", out _));
        Assert.Equal(5, settings.EnumerateObject().Count());
    }

    [Fact]
    public async Task SpeechRequest_preserves_metadata_validation()
    {
        using var metadata = JsonDocument.Parse("""{"volume":1.6}""");
        var provider = CreateProvider(_ => throw new InvalidOperationException("The upstream API must not be called."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "uvoiceai/voice-123",
            Text = "A sufficiently long test sentence.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["uvoiceai"] = metadata.RootElement.Clone()
            }
        }));
    }

    private static UVoiceAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new TestApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(new HttpClient(new TestHandler(responder))));

    private static HttpResponseMessage AudioResponse()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class TestApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
