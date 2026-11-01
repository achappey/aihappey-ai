using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.HeyGen;
using AIHappey.Core.Providers.Typecast;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderSpeechMetadataRawPassthroughTests
{
    [Fact]
    public async Task Heygen_speech_passes_namespaced_unknown_options_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        var provider = CreateHeyGenProvider(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/voices/speech")
            {
                payload = ParseElement(await request.Content!.ReadAsStringAsync());
                return JsonResponse("""{"data":{"audio_url":"/audio"}}""");
            }

            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "heygen/voice-1",
            Text = "<speak>Hello</speak>",
            Language = "fr",
            Speed = 1.5f,
            ProviderOptions = new()
            {
                ["heygen"] = ParseElement("""
                    {
                      "inputType": "ssml",
                      "speed": 0.7,
                      "language": "ja",
                      "locale": "ja-JP",
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        });

        Assert.Equal("audio", Encoding.UTF8.GetString(Convert.FromBase64String(response.Audio.Base64)));
        Assert.Equal("voice-1", payload.GetProperty("voice_id").GetString());
        Assert.Equal("<speak>Hello</speak>", payload.GetProperty("text").GetString());
        Assert.Equal("ssml", payload.GetProperty("input_type").GetString());
        Assert.Equal(1.5, payload.GetProperty("speed").GetDouble());
        Assert.Equal("fr", payload.GetProperty("language").GetString());
        Assert.Equal("ja-JP", payload.GetProperty("locale").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(
                ParseElement("""
                    {
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """).GetProperty(name),
                payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("heygen", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.False(payload.TryGetProperty("inputType", out _));
        Assert.Equal(12, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Heygen_speech_keeps_automatic_defaults_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        var provider = CreateHeyGenProvider(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/voices/speech")
            {
                payload = ParseElement(await request.Content!.ReadAsStringAsync());
                return JsonResponse("""{"data":{"audio_url":"/audio"}}""");
            }

            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "heygen/voice-1",
            Text = "Hello"
        });

        Assert.Equal("text", payload.GetProperty("input_type").GetString());
        // Null dictionary entries are serialized even with WhenWritingNull.
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("speed").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("language").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("locale").ValueKind);
        Assert.Equal(6, payload.EnumerateObject().Count());

        var invalidTypeProvider = CreateHeyGenProvider(_ =>
            throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => invalidTypeProvider.SpeechRequest(new SpeechRequest
        {
            Model = "heygen/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["heygen"] = ParseElement("""{"inputType":42}""")
            }
        }));

        var failedProvider = CreateHeyGenProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failedProvider.SpeechRequest(new SpeechRequest { Model = "heygen/voice-1", Text = "Hello" }));
        Assert.Contains("request 'v3/voices/speech' failed (400): upstream failure", error.Message);
    }

    [Theory]
    [InlineData("Hello", "text")]
    [InlineData("<speak>Hello</speak>", "ssml")]
    public async Task Heygen_speech_keeps_null_options_and_detects_input_type(string text, string inputType)
    {
        JsonElement payload = default;
        var provider = CreateHeyGenProvider(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/voices/speech")
            {
                payload = ParseElement(await request.Content!.ReadAsStringAsync());
                return JsonResponse("""{"data":{"audio_url":"/audio"}}""");
            }

            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "heygen/voice-1",
            Text = text,
            ProviderOptions = new()
            {
                ["heygen"] = ParseElement(
                    """{"inputType":null,"speed":null,"language":null,"locale":null,"future_null":null}""")
            }
        });

        Assert.Equal("voice-1", payload.GetProperty("voice_id").GetString());
        Assert.Equal(text, payload.GetProperty("text").GetString());
        Assert.Equal(inputType, payload.GetProperty("input_type").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("speed").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("language").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("locale").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("inputType", out _));
        Assert.Equal(7, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Typecast_speech_preserves_known_mapping_and_forwards_unknown_json()
    {
        JsonElement payload = default;
        var provider = CreateTypecastProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "typecast/ssfm-v30/voice-1",
            Text = "Hello",
            Language = "en",
            OutputFormat = "mp3",
            Speed = 1.5f,
            ProviderOptions = new()
            {
                ["typecast"] = ParseElement("""
                    {
                      "language": "ja",
                      "emotionType": "preset",
                      "emotion": "happy",
                      "previousText": "before",
                      "nextText": "after",
                      "volume": 150,
                      "audioPitch": 2,
                      "audioTempo": 1.25,
                      "seed": 7,
                      "prompt": {
                        "future_prompt": {"nested": [true, null, {"value": "keep"}]},
                        "emotion_type": "raw-prompt-value"
                      },
                      "output": {
                        "future_output": {"items": [1, {"enabled": true}]},
                        "volume": 99
                      },
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        });

        Assert.Equal("voice-1", payload.GetProperty("voice_id").GetString());
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("ssfm-v30", payload.GetProperty("model").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal(7, payload.GetProperty("seed").GetInt32());
        Assert.Equal("preset", payload.GetProperty("prompt").GetProperty("emotion_type").GetString());
        Assert.Equal("happy", payload.GetProperty("prompt").GetProperty("emotion").GetString());
        Assert.Equal("before", payload.GetProperty("prompt").GetProperty("previous_text").GetString());
        Assert.Equal("after", payload.GetProperty("prompt").GetProperty("next_text").GetString());
        Assert.True(payload.GetProperty("prompt").GetProperty("future_prompt").GetProperty("nested")[2]
            .GetProperty("value").GetString() == "keep");
        var output = payload.GetProperty("output");
        Assert.Equal(150, output.GetProperty("volume").GetInt32());
        Assert.Equal(2, output.GetProperty("audio_pitch").GetInt32());
        Assert.Equal(1.25, output.GetProperty("audio_tempo").GetDouble());
        Assert.Equal("mp3", output.GetProperty("audio_format").GetString());
        Assert.True(output.GetProperty("future_output").GetProperty("items")[1].GetProperty("enabled").GetBoolean());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(
                ParseElement("""
                    {
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """).GetProperty(name),
                payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("typecast", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.False(payload.TryGetProperty("speed", out _));
        Assert.Equal(13, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Typecast_speech_keeps_defaults_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        var provider = CreateTypecastProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "typecast/ssfm-v30/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["typecast"] = ParseElement(
                    """{"language":null,"volume":null,"audioPitch":null,"audioTempo":null,"emotionType":null,"seed":null,"future_null":null}""")
            }
        });

        Assert.Equal(100, payload.GetProperty("output").GetProperty("volume").GetInt32());
        Assert.Equal(0, payload.GetProperty("output").GetProperty("audio_pitch").GetInt32());
        Assert.Equal(1, payload.GetProperty("output").GetProperty("audio_tempo").GetInt32());
        Assert.Equal("wav", payload.GetProperty("output").GetProperty("audio_format").GetString());
        Assert.Equal("smart", payload.GetProperty("prompt").GetProperty("emotion_type").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("language").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("seed").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Single(payload.GetProperty("prompt").EnumerateObject());
        Assert.Equal(4, payload.GetProperty("output").EnumerateObject().Count());
        Assert.Equal(8, payload.EnumerateObject().Count());

        var invalidProvider = CreateTypecastProvider(_ =>
            throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidProvider.SpeechRequest(new SpeechRequest
        {
            Model = "typecast/model/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["typecast"] = ParseElement("""{"volume":201}""")
            }
        }));
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.SpeechRequest(new SpeechRequest
        {
            Model = "typecast/model/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["typecast"] = ParseElement("""{"emotion":42}""")
            }
        }));

        var failedProvider = CreateTypecastProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failedProvider.SpeechRequest(new SpeechRequest { Model = "typecast/model/voice-1", Text = "Hello" }));
        Assert.Contains("Typecast TTS failed (400): upstream failure", error.Message);
    }

    [Theory]
    [InlineData("ssfm-v30", true)]
    [InlineData("ssfm-v21", false)]
    public async Task Typecast_speech_keeps_model_specific_defaults_and_omits_absent_options(
        string model, bool usesDefaultPrompt)
    {
        JsonElement payload = default;
        var provider = CreateTypecastProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });

        await provider.SpeechRequest(new SpeechRequest { Model = $"typecast/{model}/voice-1", Text = "Hello" });

        Assert.Equal("voice-1", payload.GetProperty("voice_id").GetString());
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal(model, payload.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("language").ValueKind);
        Assert.False(payload.TryGetProperty("seed", out _));
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"volume":100,"audio_pitch":0,"audio_tempo":1,"audio_format":"wav"}"""),
            payload.GetProperty("output")), payload.GetRawText());
        Assert.Equal(usesDefaultPrompt, payload.TryGetProperty("prompt", out var prompt));
        if (usesDefaultPrompt)
        {
            Assert.True(JsonElement.DeepEquals(
                ParseElement("""{"emotion_type":"smart"}"""), prompt), payload.GetRawText());
        }

        Assert.Equal(usesDefaultPrompt ? 6 : 5, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Typecast_speech_does_not_replace_raw_nested_options_with_defaults()
    {
        JsonElement payload = default;
        var provider = CreateTypecastProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        var metadata = ParseElement("""
            {
              "prompt": {"emotion_type":"preset","emotion":"happy","future_null":null},
              "output": {"volume":75,"audio_pitch":-2,"audio_tempo":0.75,"audio_format":"mp3","future_null":null}
            }
            """);

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "typecast/ssfm-v30/voice-1",
            Text = "Hello",
            ProviderOptions = new() { ["typecast"] = metadata }
        });

        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("prompt"), payload.GetProperty("prompt")),
            payload.GetRawText());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("output"), payload.GetProperty("output")),
            payload.GetRawText());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("language").ValueKind);
        Assert.False(payload.TryGetProperty("seed", out _));
        Assert.Equal(6, payload.EnumerateObject().Count());
    }

    private static HeyGenProvider CreateHeyGenProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new CapturingHandler(responder))));

    private static TypecastProvider CreateTypecastProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new CapturingHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

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

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
