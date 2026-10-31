using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.VoiceAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.VoiceAI;

public sealed class VoiceAIProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task SpeechRequest_merges_provider_options_at_upstream_root_and_preserves_known_precedence()
    {
        string? upstreamBody = null;
        var metadata = JsonDocument.Parse("""
            {
              "voice_id": "metadata-voice",
              "language": "fr",
              "audio_format": "mpeg",
              "temperature": 0.25,
              "top_p": 0.7,
              "vendor_extension": {
                "nested": [1, true, null, {"leaf": "preserved"}]
              },
              "future_number": 12,
              "future_boolean": false,
              "explicit_null": null
            }
            """).RootElement.Clone();
        var request = new SpeechRequest
        {
            Model = "voiceai/voiceai-tts-v1-latest/model-voice/en",
            Text = "A sufficiently long test sentence.",
            Voice = "request-voice",
            Language = "de",
            OutputFormat = "WAVE",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["voiceai"] = metadata,
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

        Assert.True(serializedClientRequest.GetProperty("providerOptions").GetProperty("voiceai")
            .TryGetProperty("vendor_extension", out _));
        using var body = JsonDocument.Parse(Assert.IsType<string>(upstreamBody));
        var root = body.RootElement;
        Assert.Equal("voiceai-tts-v1-latest", root.GetProperty("model").GetString());
        Assert.Equal("A sufficiently long test sentence.", root.GetProperty("text").GetString());
        Assert.Equal("model-voice", root.GetProperty("voice_id").GetString());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.Equal("wav", root.GetProperty("audio_format").GetString());
        Assert.Equal(0.25, root.GetProperty("temperature").GetDouble());
        Assert.Equal(0.7, root.GetProperty("top_p").GetDouble());
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"nested":[1,true,null,{"leaf":"preserved"}]}""").RootElement,
            root.GetProperty("vendor_extension")));
        Assert.Equal(12, root.GetProperty("future_number").GetInt32());
        Assert.False(root.GetProperty("future_boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("explicit_null").ValueKind);
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("voiceai", out _));
        Assert.False(root.TryGetProperty("another-provider", out _));
    }

    [Fact]
    public async Task SpeechRequest_keeps_default_format_and_omits_null_mapped_options()
    {
        string? upstreamBody = null;
        using var metadata = JsonDocument.Parse("""
            {
              "voice_id": null,
              "language": null,
              "temperature": null,
              "top_p": null
            }
            """);
        var provider = CreateProvider(message =>
        {
            upstreamBody = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return AudioResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "voiceai/voiceai-tts-v1-latest",
            Text = "A sufficiently long test sentence.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["voiceai"] = metadata.RootElement.Clone()
            }
        });

        using var body = JsonDocument.Parse(Assert.IsType<string>(upstreamBody));
        var root = body.RootElement;
        Assert.Equal("mp3", root.GetProperty("audio_format").GetString());
        Assert.False(root.TryGetProperty("voice_id", out _));
        Assert.False(root.TryGetProperty("language", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("top_p", out _));
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    private static VoiceAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new TestApiKeyResolver(), new TestHttpClientFactory(new HttpClient(new TestHandler(responder))));

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
