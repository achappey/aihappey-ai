using System.Net;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.UnrealSpeech;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.UnrealSpeech;

public sealed class UnrealSpeechProviderSpeechTests
{
    [Fact]
    public async Task Speech_request_forwards_unknown_provider_options_unchanged_at_the_upstream_root()
    {
        JsonElement payload = default;
        var metadata = ParseElement("""
            {
              "bitrate": "64k",
              "speed": 0.5,
              "pitch": 1.2,
              "codec": "pcm_mulaw",
              "temperature": 0.4,
              "futureObject": {"nested": [{"value": 1}, [true, null, "text"]]},
              "futureArray": [1, {"more": [false, 2.5]}, []],
              "futureString": "unchanged",
              "futureNumber": 12345678901234567890.123456789,
              "futureBoolean": false,
              "futureNull": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "Samantha",
            Text = "Hello!",
            ProviderOptions = new() { ["unrealspeech"] = metadata }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(metadata, clientBoundary.GetProperty("providerOptions").GetProperty("unrealspeech")));
        var provider = CreateProvider(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2])
            };
        });

        await provider.SpeechRequest(request);

        Assert.Equal("Hello!", payload.GetProperty("Text").GetString());
        Assert.Equal("Samantha", payload.GetProperty("VoiceId").GetString());
        Assert.Equal("64k", payload.GetProperty("Bitrate").GetString());
        Assert.Equal(0.5f, payload.GetProperty("Speed").GetSingle());
        Assert.Equal(1.2f, payload.GetProperty("Pitch").GetSingle());
        Assert.Equal("pcm_mulaw", payload.GetProperty("Codec").GetString());
        Assert.Equal(0.4f, payload.GetProperty("Temperature").GetSingle());
        foreach (var name in new[]
                 {
                     "futureObject", "futureArray", "futureString", "futureNumber", "futureBoolean", "futureNull"
                 })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("unrealspeech", out _));
        Assert.Equal(13, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_request_preserves_defaults_and_known_option_validation()
    {
        var provider = CreateProvider(message =>
        {
            var payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("192k", payload.GetProperty("Bitrate").GetString());
            Assert.Equal(0f, payload.GetProperty("Speed").GetSingle());
            Assert.Equal(1f, payload.GetProperty("Pitch").GetSingle());
            Assert.False(payload.TryGetProperty("Codec", out _));
            Assert.False(payload.TryGetProperty("Temperature", out _));
            Assert.Equal(5, payload.EnumerateObject().Count());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2])
            };
        });

        await provider.SpeechRequest(new SpeechRequest { Model = "Samantha", Text = "Hello!" });

        var invalidProvider = CreateProvider(_ => throw new InvalidOperationException("HTTP must not be called."));
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            invalidProvider.SpeechRequest(new SpeechRequest
            {
                Model = "Samantha",
                Text = "Hello!",
                ProviderOptions = new()
                {
                    ["unrealspeech"] = ParseElement("""{"bitrate":"1k"}""")
                }
            }));
        Assert.Contains("UnrealSpeech bitrate must be one of", error.Message);
        Assert.Equal("Bitrate", error.ParamName);
    }

    private static UnrealSpeechProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

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

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
