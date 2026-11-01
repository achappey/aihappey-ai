using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Sarvam;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Sarvam;

public sealed class SarvamProviderOpenAISpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_existing_sarvam_speech_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioBytes = Encoding.UTF8.GetBytes("sarvam-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        request_id = "req-1",
                        audios = new[] { Convert.ToBase64String(audioBytes) }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "sarvam/bulbul:v3",
            Voice = "shubh",
            Input = "Hello from Sarvam OpenAI compatibility!",
            ResponseFormat = "mp3"
        });

        Assert.Equal(audioBytes, audio);
        Assert.Equal("audio/mpeg", mimeType);
        Assert.NotNull(capturedRequest);
    }

    [Fact]
    public async Task SpeechRequest_passes_unknown_provider_options_at_root_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        var client = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/text-to-speech", StringComparison.Ordinal))
                payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { audios = new[] { Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")) } }),
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var metadata = ParseElement("""
            {
              "language_code": "bn-IN",
              "speaker": "metadata-speaker",
              "pitch": 0.1,
              "pace": 0.7,
              "loudness": 1.1,
              "speech_sample_rate": 22050,
              "enable_preprocessing": true,
              "model": "bulbul:v3",
              "output_audio_codec": "wav",
              "temperature": 0.2,
              "dict_id": "dictionary",
              "enable_cached_responses": false,
              "output_audio_bitrate": "128kbps",
              "text": "metadata text",
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
            Model = "sarvam/bulbul:v3",
            Text = "Request text",
            Language = "hi-IN",
            Voice = "request-speaker",
            Speed = 1.25f,
            OutputFormat = "mp3",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["sarvam"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":true}""")
            }
        };
        var serializedBefore = JsonSerializer.SerializeToElement(request);

        await client.SpeechRequest(request);

        Assert.Equal("Request text", payload.GetProperty("text").GetString());
        Assert.Equal("hi-IN", payload.GetProperty("language_code").GetString());
        Assert.Equal("request-speaker", payload.GetProperty("speaker").GetString());
        Assert.Equal("bulbul:v3", payload.GetProperty("model").GetString());
        Assert.Equal("mp3", payload.GetProperty("output_audio_codec").GetString());
        // Explicit Sarvam pace has always taken precedence; unified speed is its fallback.
        Assert.Equal(0.7, payload.GetProperty("pace").GetDouble());
        Assert.Equal(22050, payload.GetProperty("speech_sample_rate").GetInt32());
        Assert.Equal(0.2, payload.GetProperty("temperature").GetDouble());
        Assert.Equal("dictionary", payload.GetProperty("dict_id").GetString());
        Assert.False(payload.GetProperty("enable_cached_responses").GetBoolean());
        Assert.Equal("128kbps", payload.GetProperty("output_audio_bitrate").GetString());
        Assert.False(payload.TryGetProperty("pitch", out _));
        Assert.False(payload.TryGetProperty("loudness", out _));
        Assert.False(payload.TryGetProperty("enable_preprocessing", out _));
        Assert.Equal(
            new[]
            {
                "text", "language_code", "speaker", "model", "pace", "speech_sample_rate", "output_audio_codec",
                "temperature", "dict_id", "enable_cached_responses", "output_audio_bitrate", "future_object",
                "future_array", "future_string", "future_number", "future_boolean", "future_null"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("sarvam", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.True(JsonElement.DeepEquals(metadata,
            serializedBefore.GetProperty("providerOptions").GetProperty("sarvam")));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request), serializedBefore));
    }

    [Theory]
    [InlineData("{\"pace\":0.7}", 1.25f, 0.7)]
    [InlineData("{\"pace\":0.7}", null, 0.7)]
    [InlineData("{}", 1.25f, 1.25)]
    [InlineData("{\"pace\":null}", 1.25f, 1.25)]
    [InlineData(null, 1.25f, 1.25)]
    [InlineData("{}", null, null)]
    [InlineData("{\"pace\":null}", null, null)]
    [InlineData(null, null, null)]
    public async Task SpeechRequest_preserves_explicit_pace_speed_fallback_and_omission(
        string? metadataJson, float? speed, double? expectedPace)
    {
        JsonElement payload = default;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/text-to-speech", StringComparison.Ordinal))
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"audios":["AQID"]}""", Encoding.UTF8, "application/json")
            };
        });
        var request = new SpeechRequest
        {
            Model = "sarvam/bulbul:v3",
            Text = "Hello",
            Speed = speed,
            ProviderOptions = metadataJson is null
                ? null
                : new Dictionary<string, JsonElement> { ["sarvam"] = ParseElement(metadataJson) }
        };
        var serializedBefore = JsonSerializer.SerializeToElement(request);

        await provider.SpeechRequest(request);

        if (expectedPace is { } pace)
            Assert.Equal(pace, payload.GetProperty("pace").GetDouble());
        else
            Assert.False(payload.TryGetProperty("pace", out _));

        Assert.True(JsonElement.DeepEquals(serializedBefore, JsonSerializer.SerializeToElement(request)));
    }

    [Fact]
    public async Task SpeechRequest_preserves_defaults_omission_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/text-to-speech", StringComparison.Ordinal))
                payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"audios":["AQID"]}""", Encoding.UTF8, "application/json")
            };
        });
        var request = new SpeechRequest
        {
            Model = "sarvam/bulbul:v3",
            Text = "Hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["sarvam"] = ParseElement(
                    """{"speaker":null,"pitch":null,"pace":null,"loudness":null,"speech_sample_rate":null,"enable_preprocessing":null,"output_audio_codec":null,"temperature":null,"dict_id":null,"enable_cached_responses":null,"output_audio_bitrate":null,"future_null":null}""")
            }
        };

        await provider.SpeechRequest(request);

        Assert.Equal("en-IN", payload.GetProperty("language_code").GetString());
        Assert.Equal("bulbul:v3", payload.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(4, payload.EnumerateObject().Count());

        var invalidProvider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions!["sarvam"] = ParseElement("""{"pitch":"invalid"}""");
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.SpeechRequest(request));

        // A valid unified speed must not bypass validation of an explicit provider pace.
        request.Speed = 1.25f;
        request.ProviderOptions["sarvam"] = ParseElement("""{"pace":"invalid"}""");
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.SpeechRequest(request));
        request.Speed = null;

        var failedProvider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        request.ProviderOptions!["sarvam"] = ParseElement("{}");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failedProvider.SpeechRequest(request));
        Assert.Equal("Sarvam TTS failed (400): upstream failure", error.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_sarvam_stream_request_and_maps_binary_chunks_to_openai_events()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioChunk = Encoding.UTF8.GetBytes("streamed-sarvam-audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioChunk)
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "bulbul:v3",
                           Voice = "priya",
                           Input = "Hello streamed Sarvam!",
                           ResponseFormat = "mp3"
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first =>
            {
                var delta = Assert.IsType<AudioSpeechStreamDelta>(first);
                Assert.Equal("speech.audio.delta", delta.Type);
                Assert.Equal(Convert.ToBase64String(audioChunk), delta.Audio);
            },
            second =>
            {
                var done = Assert.IsType<AudioSpeechStreamDone>(second);
                Assert.Equal("speech.audio.done", done.Type);
                Assert.Null(done.Usage);
            });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/text-to-speech/stream", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Contains("audio/mpeg", capturedRequest.Headers.Accept.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues("api-subscription-key", out var values));
        Assert.Equal("test-api-key", Assert.Single(values));

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Hello streamed Sarvam!", root.GetProperty("text").GetString());
        Assert.Equal("en-IN", root.GetProperty("language_code").GetString());
        Assert.False(root.TryGetProperty("target_language_code", out _));
        Assert.Equal("priya", root.GetProperty("speaker").GetString());
        Assert.Equal("bulbul:v3", root.GetProperty("model").GetString());
        Assert.Equal("mp3", root.GetProperty("output_audio_codec").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_sarvam_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { message = "Text exceeds maximum length of 3500 characters", code = "unprocessable_entity_error" } }),
                Encoding.UTF8,
                "application/json")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "bulbul:v3",
                               Voice = "shubh",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Sarvam streaming TTS failed (422)", exception.Message);
        Assert.Contains("Text exceeds maximum length", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_input()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "bulbul:v3"
                           }))
            {
            }
        });

        Assert.Contains("Input is required", exception.Message);
    }

    private static SarvamProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));

        return new SarvamProvider(new StaticApiKeyResolver(), httpClientFactory);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
