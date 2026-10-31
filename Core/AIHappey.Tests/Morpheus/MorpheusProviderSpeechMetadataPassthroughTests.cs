using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Morpheus;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Morpheus;

public sealed class MorpheusProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_forwards_unknown_options_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/api/v1/audio/speech", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });
        var metadata = ParseElement("""
            {
              "model": "metadata-model",
              "input": "metadata input",
              "voice": "MetadataVoice",
              "response_format": "flac",
              "speed": 0.5,
              "session_id": " metadata-session ",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "morpheus/model",
            Text = "request input",
            Voice = "RequestVoice",
            OutputFormat = "mp3",
            Speed = 1.25f,
            ProviderOptions = new()
            {
                ["morpheus"] = metadata,
                ["other-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        await CreateProvider(client).SpeechRequest(request);

        var root = payload;
        Assert.Equal("morpheus/model", root.GetProperty("model").GetString());
        Assert.Equal("request input", root.GetProperty("input").GetString());
        Assert.Equal("RequestVoice", root.GetProperty("voice").GetString());
        Assert.Equal("mp3", root.GetProperty("response_format").GetString());
        Assert.Equal(1.25f, root.GetProperty("speed").GetSingle());
        Assert.Equal("metadata-session", root.GetProperty("session_id").GetString());
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "model" or "input" or "voice" or "response_format" or "speed" or "session_id")
                continue;

            Assert.True(JsonElement.DeepEquals(property.Value, root.GetProperty(property.Name)), property.Name);
        }

        Assert.True(JsonElement.DeepEquals(
            metadata,
            serializedClientRequest.GetProperty("providerOptions").GetProperty("morpheus")));
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("morpheus", out _));
        Assert.False(root.TryGetProperty("other-provider", out _));
        Assert.False(root.TryGetProperty("must_not_leak", out _));
    }

    [Fact]
    public async Task Speech_request_keeps_defaults_and_preserves_null_session_id()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });
        var request = new SpeechRequest
        {
            Model = "morpheus/model",
            Text = "hello",
            ProviderOptions = new()
            {
                ["morpheus"] = ParseElement("""
                    {
                      "voice": null,
                      "response_format": null,
                      "speed": null,
                      "session_id": null,
                      "future_null": null
                    }
                    """)
            }
        };

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("af_alloy", payload.GetProperty("voice").GetString());
        Assert.Equal("mp3", payload.GetProperty("response_format").GetString());
        Assert.Equal(1f, payload.GetProperty("speed").GetSingle());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("session_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(7, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_request_preserves_invalid_known_option_error_behavior()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new SpeechRequest
        {
            Model = "morpheus/model",
            Text = "hello",
            ProviderOptions = new()
            {
                ["morpheus"] = ParseElement("""{"speed":"fast"}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(client).SpeechRequest(request));
    }

    [Fact]
    public async Task Speech_request_preserves_upstream_error_details()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var request = new SpeechRequest
        {
            Model = "morpheus/model",
            Text = "hello"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider(client).SpeechRequest(request));

        Assert.Equal("Morpheus speech request failed (400): upstream failure", exception.Message);
    }

    private static MorpheusProvider CreateProvider(HttpClient client)
        => new(new TestApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage AudioResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new TestHandler(responder));

    private sealed class TestApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
