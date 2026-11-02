using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Gradium;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Gradium;

public sealed class GradiumProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_forwards_unknown_provider_metadata_at_root_and_preserves_known_mappings()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/api/post/speech/tts", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });
        var metadata = ParseElement(
            """
            {
              "voice_id": "metadata-voice",
              "model_name": " custom-model ",
              "output_format": "wave",
              "json_config": "{\"custom\":true}",
              "only_audio": false,
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
            Model = "experimental/model-voice",
            Text = "Hello Gradium",
            Voice = "request-voice",
            ProviderOptions = new()
            {
                ["gradium"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        await CreateProvider(client).SpeechRequest(request);

        var providerOptions = serializedClientRequest.GetProperty("providerOptions");
        Assert.True(JsonElement.DeepEquals(metadata, providerOptions.GetProperty("gradium")));
        Assert.True(providerOptions.TryGetProperty("another-provider", out _));
        Assert.Equal("Hello Gradium", payload.GetProperty("text").GetString());
        Assert.Equal("model-voice", payload.GetProperty("voice_id").GetString());
        Assert.Equal("custom-model", payload.GetProperty("model_name").GetString());
        Assert.Equal("wav", payload.GetProperty("output_format").GetString());
        Assert.Equal("{\"custom\":true}", payload.GetProperty("json_config").GetString());
        Assert.True(payload.GetProperty("only_audio").GetBoolean());
        Assert.Equal(12, payload.EnumerateObject().Count());
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "voice_id" or "model_name" or "output_format" or "json_config" or "only_audio")
                continue;

            if (property.Name == "future_number")
            {
                Assert.Equal(property.Value.GetRawText(), payload.GetProperty(property.Name).GetRawText());
                continue;
            }

            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("gradium", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
    }

    [Fact]
    public async Task Speech_request_preserves_defaults_omission_rules_and_known_property_overrides()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });
        var request = new SpeechRequest
        {
            Model = "default",
            Text = "Hello Gradium",
            Voice = "request-voice",
            ProviderOptions = new()
            {
                ["gradium"] = ParseElement(
                    """{"voice_id":null,"model_name":null,"output_format":null,"json_config":null,"only_audio":null,"future_null":null}""")
            }
        };

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("request-voice", payload.GetProperty("voice_id").GetString());
        Assert.Equal("wav", payload.GetProperty("output_format").GetString());
        Assert.True(payload.GetProperty("only_audio").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("model_name", out _));
        Assert.False(payload.TryGetProperty("json_config", out _));
        Assert.Equal(5, payload.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"voice_id":42}""")]
    [InlineData("""{"only_audio":"true"}""")]
    public async Task Speech_request_preserves_invalid_metadata_error_behavior(string metadataJson)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new SpeechRequest
        {
            Model = "default/voice",
            Text = "Hello Gradium",
            ProviderOptions = new() { ["gradium"] = ParseElement(metadataJson) }
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
            Model = "default/voice",
            Text = "Hello Gradium"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider(client).SpeechRequest(request));

        Assert.Equal("Gradium TTS failed (400): upstream failure", exception.Message);
    }

    private static GradiumProvider CreateProvider(HttpClient client)
        => new(new TestApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(client));

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
