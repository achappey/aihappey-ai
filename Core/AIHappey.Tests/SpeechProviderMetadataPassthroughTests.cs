using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.LOVO;
using AIHappey.Core.Providers.TTSReader;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class SpeechProviderMetadataPassthroughTests
{
    [Fact]
    public async Task TTSReader_forwards_unknown_options_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/ttsSync", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var metadata = ParseElement("""
            {
              "voice": "MetadataVoice",
              "language": "fr",
              "rate": 0.75,
              "quality": "high",
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
            Model = "ttsreader/standard",
            Text = "hello",
            Voice = "RequestVoice",
            Language = "en-US",
            Speed = 1.25f,
            ProviderOptions = new()
            {
                ["ttsreader"] = metadata,
                ["other-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateTTSReaderProvider(client).SpeechRequest(request);

        Assert.Equal("hello", payload.GetProperty("text").GetString());
        Assert.Equal("RequestVoice", payload.GetProperty("voice").GetString());
        Assert.Equal("en-US", payload.GetProperty("language").GetString());
        Assert.Equal(1.25f, payload.GetProperty("rate").GetSingle());
        Assert.Equal("high", payload.GetProperty("quality").GetString());
        AssertMetadataUnknownFieldsForwarded(metadata, payload, "voice", "language", "rate", "quality");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("ttsreader", out _));
        Assert.False(payload.TryGetProperty("other-provider", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["ttsreader"]));
    }

    [Fact]
    public async Task TTSReader_uses_metadata_fallbacks_and_omits_absent_optional_values()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
        });
        var request = new SpeechRequest
        {
            Model = "ttsreader/standard",
            Text = "hello",
            ProviderOptions = new()
            {
                ["ttsreader"] = ParseElement("""{"voice":"MetaVoice","language":"de","rate":null,"quality":" ","unknown":null}""")
            }
        };

        await CreateTTSReaderProvider(client).SpeechRequest(request);

        Assert.Equal("MetaVoice", payload.GetProperty("voice").GetString());
        Assert.Equal("de", payload.GetProperty("language").GetString());
        Assert.False(payload.TryGetProperty("rate", out _));
        Assert.False(payload.TryGetProperty("quality", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("unknown").ValueKind);
    }

    [Fact]
    public async Task TTSReader_keeps_provider_error_message_compatible()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid request")
        });
        var request = new SpeechRequest
        {
            Model = "ttsreader/standard",
            Text = "hello",
            Voice = "voice",
            Language = "en"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateTTSReaderProvider(client).SpeechRequest(request));

        Assert.Equal("TTSReader TTS failed (400): invalid request", error.Message);
    }

    [Fact]
    public async Task TTSReader_invalid_known_option_type_still_fails_before_sending()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new SpeechRequest
        {
            Model = "ttsreader/standard",
            Text = "hello",
            Voice = "voice",
            Language = "en",
            ProviderOptions = new()
            {
                ["ttsreader"] = ParseElement("""{"rate":"fast"}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(
            () => CreateTTSReaderProvider(client).SpeechRequest(request));
    }

    [Fact]
    public async Task LOVO_forwards_unknown_options_at_root_and_preserves_known_mapping()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/v1/tts/sync", request.RequestUri!.AbsolutePath);
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"audioUrl":"https://audio.example/result.mp3"}""");
            }

            Assert.Equal("https://audio.example/result.mp3", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 5, 6])
            };
        });
        var metadata = ParseElement("""
            {
              "speakerStyle": "  cheerful  ",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "text": "metadata text",
              "speed": 0.4,
              "speaker": "metadata speaker"
            }
            """);
        var request = new SpeechRequest
        {
            Model = "tts/speaker-id",
            Text = "request text",
            Speed = 1.5f,
            ProviderOptions = new()
            {
                ["lovo"] = metadata,
                ["other-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateLOVOProvider(client).SpeechRequest(request);

        Assert.Equal("request text", payload.GetProperty("text").GetString());
        Assert.Equal("speaker-id", payload.GetProperty("speaker").GetString());
        Assert.Equal(1.5f, payload.GetProperty("speed").GetSingle());
        Assert.Equal("cheerful", payload.GetProperty("speakerStyle").GetString());
        AssertMetadataUnknownFieldsForwarded(metadata, payload, "speakerStyle");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("lovo", out _));
        Assert.False(payload.TryGetProperty("other-provider", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["lovo"]));
    }

    [Fact]
    public async Task LOVO_omits_blank_known_options_but_forwards_unknown_nulls()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"audioUrl":"https://audio.example/result.mp3"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
        });
        var request = new SpeechRequest
        {
            Model = "tts/speaker-id",
            Text = "hello",
            ProviderOptions = new()
            {
                ["lovo"] = ParseElement("""{"speakerStyle":" ","future_null":null}""")
            }
        };

        await CreateLOVOProvider(client).SpeechRequest(request);

        Assert.False(payload.TryGetProperty("speakerStyle", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(1f, payload.GetProperty("speed").GetSingle());
    }

    private static void AssertMetadataUnknownFieldsForwarded(
        JsonElement source,
        JsonElement payload,
        params string[] replacedProperties)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (replacedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            if (property.Name is "text" or "speed" or "speaker")
                continue;

            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }
    }

    private static TTSReaderProvider CreateTTSReaderProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static LOVOProvider CreateLOVOProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
