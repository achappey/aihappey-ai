using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.YourVoic;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.YourVoic;

public sealed class YourVoicProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_forwards_unknown_options_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/api/v1/tts/generate", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });
        var metadata = ParseElement("""
            {
              "format": "mpeg",
              "pitch": 1.25,
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
            Model = "yourvoic/model",
            Text = "hello",
            Voice = "request-voice",
            Language = "en",
            Speed = 1.1f,
            OutputFormat = "wav",
            ProviderOptions = new()
            {
                ["yourvoic"] = metadata,
                ["other-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("yourvoic/model", payload.GetProperty("model").GetString());
        Assert.Equal("hello", payload.GetProperty("text").GetString());
        Assert.Equal("request-voice", payload.GetProperty("voice").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal(1.1f, payload.GetProperty("speed").GetSingle());
        Assert.Equal("wav", payload.GetProperty("format").GetString());
        Assert.Equal(1.25f, payload.GetProperty("pitch").GetSingle());
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "format" or "pitch")
                continue;

            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }

        Assert.True(JsonElement.DeepEquals(
            metadata,
            serializedClientRequest.GetProperty("providerOptions").GetProperty("yourvoic")));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("yourvoic", out _));
        Assert.False(payload.TryGetProperty("other-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
    }

    [Fact]
    public async Task Speech_request_keeps_optional_fields_omitted_and_passes_explicit_unknown_null()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });
        var request = new SpeechRequest
        {
            Model = "yourvoic/model",
            Text = "hello",
            ProviderOptions = new()
            {
                ["yourvoic"] = ParseElement("""{"format":null,"pitch":null,"future_null":null}""")
            }
        };

        await CreateProvider(client).SpeechRequest(request);

        Assert.False(payload.TryGetProperty("pitch", out _));
        Assert.False(payload.TryGetProperty("format", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(3, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_request_preserves_invalid_known_option_error_behavior()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new SpeechRequest
        {
            Model = "yourvoic/model",
            Text = "hello",
            ProviderOptions = new()
            {
                ["yourvoic"] = ParseElement("""{"pitch":"fast"}""")
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
        var request = new SpeechRequest { Model = "yourvoic/model", Text = "hello" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider(client).SpeechRequest(request));

        Assert.Equal("YourVoic TTS failed (400): upstream failure", exception.Message);
    }

    private static YourVoicProvider CreateProvider(HttpClient client)
        => new(new TestApiKeyResolver(), new TestHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage AudioResponse()
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

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
