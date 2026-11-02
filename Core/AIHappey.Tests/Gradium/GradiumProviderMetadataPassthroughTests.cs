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

    [Fact]
    public async Task Transcription_request_forwards_unknown_options_and_maps_known_options()
    {
        Uri? requestUri = null;
        using var client = CreateClient(message =>
        {
            requestUri = message.RequestUri;
            return TranscriptionResponse();
        });
        var metadata = ParseElement(
            """
            {
              "language": " en ",
              "temperature": 0.25,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "gradium/default",
            MediaType = "audio/wav",
            Audio = Convert.ToBase64String([1, 2, 3]),
            ProviderOptions = new()
            {
                ["gradium"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal(" en ", response.Language);
        Assert.NotNull(requestUri);
        Assert.Equal("/api/post/speech/asr", requestUri!.AbsolutePath);
        var configParameter = requestUri.Query.Split('&')
            .Single(value => value.StartsWith("json_config=", StringComparison.Ordinal));
        using var configDocument = JsonDocument.Parse(Uri.UnescapeDataString(configParameter["json_config=".Length..]));
        var config = configDocument.RootElement;
        Assert.Equal("en", config.GetProperty("language").GetString());
        Assert.Equal(0.25f, config.GetProperty("temp").GetSingle());
        Assert.False(config.TryGetProperty("temperature", out _));
        Assert.Equal(8, config.EnumerateObject().Count());

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "language" or "temperature")
                continue;

            Assert.True(JsonElement.DeepEquals(property.Value, config.GetProperty(property.Name)), property.Name);
        }

        var providerOptions = serializedClientRequest.GetProperty("providerOptions");
        Assert.True(JsonElement.DeepEquals(metadata, providerOptions.GetProperty("gradium")));
        Assert.True(providerOptions.TryGetProperty("another-provider", out _));
        Assert.False(config.TryGetProperty("providerOptions", out _));
        Assert.False(config.TryGetProperty("gradium", out _));
        Assert.False(config.TryGetProperty("must_not_leak", out _));
    }

    [Fact]
    public async Task Transcription_request_preserves_omission_rules_and_validates_known_metadata()
    {
        Uri? requestUri = null;
        using var client = CreateClient(message =>
        {
            requestUri = message.RequestUri;
            return TranscriptionResponse();
        });

        await CreateProvider(client).TranscriptionRequest(
            CreateTranscriptionRequest("""{"language":null,"temperature":null,"future_null":null}"""));

        Assert.NotNull(requestUri);
        var configParameter = requestUri!.Query.Split('&')
            .Single(value => value.StartsWith("json_config=", StringComparison.Ordinal));
        using var configDocument = JsonDocument.Parse(Uri.UnescapeDataString(configParameter["json_config=".Length..]));
        var config = configDocument.RootElement;
        Assert.Equal(1, config.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("future_null").ValueKind);
        Assert.False(config.TryGetProperty("language", out _));
        Assert.False(config.TryGetProperty("temp", out _));

        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(
            () => CreateProvider(unusedClient).TranscriptionRequest(CreateTranscriptionRequest("[]")));
        await Assert.ThrowsAsync<JsonException>(
            () => CreateProvider(unusedClient).TranscriptionRequest(CreateTranscriptionRequest("""{"temperature":"high"}""")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateProvider(unusedClient).TranscriptionRequest(CreateTranscriptionRequest("""{"temperature":1.1}""")));
    }

    [Fact]
    public async Task Transcription_request_preserves_upstream_error_details()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider(client).TranscriptionRequest(CreateTranscriptionRequest(null)));

        Assert.Equal("Gradium transcription failed (400): upstream failure", exception.Message);
    }

    private static GradiumProvider CreateProvider(HttpClient client)
        => new(new TestApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(client));

    private static TranscriptionRequest CreateTranscriptionRequest(string? metadataJson)
        => new()
        {
            Model = "gradium/default",
            MediaType = "audio/wav",
            Audio = Convert.ToBase64String([1, 2, 3]),
            ProviderOptions = metadataJson is null
                ? null
                : new Dictionary<string, JsonElement> { ["gradium"] = ParseElement(metadataJson) }
        };

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage AudioResponse()
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

    private static HttpResponseMessage TranscriptionResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"type":"text","text":"recognized"}""" + "\n" + """{"type":"end_text","stop_s":1}""",
                Encoding.UTF8,
                "application/x-ndjson")
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
