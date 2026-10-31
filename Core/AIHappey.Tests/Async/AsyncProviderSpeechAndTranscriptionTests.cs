using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Async;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Async;

public sealed class AsyncProviderSpeechAndTranscriptionTests
{
    [Fact]
    public async Task SpeechRequest_uses_shortcut_voice_and_base_model_id()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var provider = CreateProvider(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-binary"))
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "async_flash_v1.0/voice-shortcut",
            Voice = "explicit-voice",
            Text = "hello world"
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("/text_to_speech", capturedRequest!.RequestUri?.AbsolutePath);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("async_flash_v1.0", doc.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("voice-shortcut", doc.RootElement.GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("mp3", doc.RootElement.GetProperty("output_format").GetProperty("container").GetString());
        Assert.Equal(44100, doc.RootElement.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
        Assert.Equal(192000, doc.RootElement.GetProperty("output_format").GetProperty("bit_rate").GetInt32());
        Assert.False(doc.RootElement.GetProperty("output_format").TryGetProperty("encoding", out _));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("language").ValueKind);
        Assert.False(doc.RootElement.TryGetProperty("speed_control", out _));

        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Contains(response.Warnings, warning => JsonSerializer.Serialize(warning).Contains("voice is derived from model id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SpeechRequest_preserves_unknown_provider_options_and_keeps_known_overrides()
    {
        JsonElement payload = default;
        var provider = CreateProvider(async request =>
        {
            payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });
        var metadata = JsonDocument.Parse("""
            {
              "voice": {"mode":"custom","id":"metadata-voice","future_voice":{"values":[1,null,true]}},
              "output_format": {
                "container":"wav",
                "sample_rate":22050,
                "bit_rate":128000,
                "future_format":{"nested":[{"flag":false}]}
              },
              "language":"fr",
              "speed_control":0.8,
              "stability":75,
              "future_object":{"nested":[{"value":1},[true,null,"text"]]},
              "future_array":[1,{"more":[false,2.5]},[]],
              "future_string":"unchanged",
              "future_number":12345678901234567890.123456789,
              "future_boolean":false,
              "future_null":null,
              "model_id":"must-not-override",
              "transcript":"must-not-override"
            }
            """).RootElement.Clone();

        var speechRequest = new SpeechRequest
        {
            Model = "async_flash_v1.0",
            Voice = "request-voice",
            Text = "request text",
            OutputFormat = "mp3",
            Language = "en",
            Speed = 1.25f,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["async"] = metadata,
                ["another-provider"] = JsonDocument.Parse("""{"secret":"not-forwarded"}""").RootElement.Clone()
            }
        };
        await provider.SpeechRequest(speechRequest);

        Assert.Equal("async_flash_v1.0", payload.GetProperty("model_id").GetString());
        Assert.Equal("request text", payload.GetProperty("transcript").GetString());
        Assert.Equal("request-voice", payload.GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("id", payload.GetProperty("voice").GetProperty("mode").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("voice").GetProperty("future_voice"),
            payload.GetProperty("voice").GetProperty("future_voice")));
        Assert.Equal("mp3", payload.GetProperty("output_format").GetProperty("container").GetString());
        Assert.Equal(22050, payload.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
        Assert.Equal(128000, payload.GetProperty("output_format").GetProperty("bit_rate").GetInt32());
        Assert.False(payload.GetProperty("output_format").TryGetProperty("encoding", out _));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("output_format").GetProperty("future_format"),
            payload.GetProperty("output_format").GetProperty("future_format")));
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal(1.25, payload.GetProperty("speed_control").GetDouble(), precision: 2);
        Assert.Equal(75, payload.GetProperty("stability").GetInt32());
        foreach (var name in new[] { "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null" })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("async", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.True(JsonElement.DeepEquals(metadata, speechRequest.ProviderOptions["async"]));
        Assert.True(speechRequest.ProviderOptions.ContainsKey("another-provider"));
    }

    [Fact]
    public async Task SpeechRequest_keeps_invalid_known_metadata_types_as_json_errors()
    {
        var provider = CreateProvider((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("HTTP should not be called.")));

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "async_flash_v1.0",
            Text = "hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["async"] = JsonSerializer.SerializeToElement(new { stability = "high" })
            }
        }));
    }

    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_existing_async_speech_request_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var provider = CreateProvider(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wav-audio"))
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return response;
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "async_flash_v1.0/voice-shortcut",
            Voice = "explicit-voice",
            Input = "hello openai",
            ResponseFormat = "wav"
        });

        Assert.Equal(Encoding.UTF8.GetBytes("wav-audio"), audio);
        Assert.Equal("audio/wav", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal("/text_to_speech", capturedRequest!.RequestUri?.AbsolutePath);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("async_flash_v1.0", doc.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("voice-shortcut", doc.RootElement.GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("hello openai", doc.RootElement.GetProperty("transcript").GetString());
        Assert.Equal("wav", doc.RootElement.GetProperty("output_format").GetProperty("container").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_native_streaming_request_and_maps_bytes_to_openai_events()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var audioChunk = Encoding.UTF8.GetBytes("streamed-audio");

        var provider = CreateProvider(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioChunk)
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return response;
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "async_flash_v1.5",
                           Voice = "voice-id",
                           Input = "hello streamed async",
                           ResponseFormat = "pcm",
                           Speed = 1.2f
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
        Assert.Equal("/text_to_speech/streaming", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.Contains("application/octet-stream", capturedRequest.Headers.Accept.ToString());

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("async_flash_v1.5", doc.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("voice-id", doc.RootElement.GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("hello streamed async", doc.RootElement.GetProperty("transcript").GetString());
        Assert.Equal("raw", doc.RootElement.GetProperty("output_format").GetProperty("container").GetString());
        Assert.Equal("pcm_s16le", doc.RootElement.GetProperty("output_format").GetProperty("encoding").GetString());
        Assert.Equal(44100, doc.RootElement.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
        Assert.Equal(1.2f, doc.RootElement.GetProperty("speed_control").GetSingle(), precision: 2);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_uses_non_streaming_fallback_for_unsupported_streaming_format()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wav-audio"))
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return response;
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "async_flash_v1.0/voice-shortcut",
                           Input = "hello wav fallback",
                           ResponseFormat = "wav"
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first =>
            {
                var delta = Assert.IsType<AudioSpeechStreamDelta>(first);
                Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("wav-audio")), delta.Audio);
            },
            second => Assert.IsType<AudioSpeechStreamDone>(second));

        Assert.NotNull(capturedRequest);
        Assert.Equal("/text_to_speech", capturedRequest!.RequestUri?.AbsolutePath);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("wav", doc.RootElement.GetProperty("output_format").GetProperty("container").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_voice_for_native_streaming_base_model()
    {
        var provider = CreateProvider((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("HTTP should not be called.")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "async_flash_v1.0",
                               Input = "hello",
                               ResponseFormat = "mp3"
                           }))
            {
            }
        });

        Assert.Contains("voice", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_async_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid voice", Encoding.UTF8, "text/plain")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "async_flash_v1.0/voice-shortcut",
                               Input = "hello",
                               ResponseFormat = "mp3"
                           }))
            {
            }
        });

        Assert.Contains("asyncAI streaming TTS failed (400): invalid voice", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_when_async_reports_quota_exceeded_mid_stream()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("--ERROR:QUOTA_EXCEEDED--"))
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "async_flash_v1.0/voice-shortcut",
                               Input = "hello",
                               ResponseFormat = "mp3"
                           }))
            {
            }
        });

        Assert.Contains("quota exceeded", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscriptionRequest_posts_multipart_payload_and_raw_metadata_passthrough()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            return JsonResponse(
                """
                {
                  "text":"hello there"
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "async/async_asr_v1.0",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["async"] = JsonSerializer.SerializeToElement(new
                {
                    language = "en",
                    custom_flag = true,
                    temperature = 0.2,
                    model_id = "should-not-override"
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("/speech_to_text", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.StartsWith("multipart/form-data", capturedRequest.Content!.Headers.ContentType?.MediaType);

        var body = capturedBody!;
        Assert.Contains("name=model_id", body, StringComparison.Ordinal);
        Assert.Contains("async_asr_v1.0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-override", body, StringComparison.Ordinal);
        Assert.Contains("name=language", body, StringComparison.Ordinal);
        Assert.Contains("en", body, StringComparison.Ordinal);
        Assert.Contains("name=custom_flag", body, StringComparison.Ordinal);
        Assert.Contains("true", body, StringComparison.Ordinal);
        Assert.Contains("name=temperature", body, StringComparison.Ordinal);
        Assert.Contains("0.2", body, StringComparison.Ordinal);

        Assert.Equal("hello there", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal("async/async_asr_v1.0", response.Response.ModelId);
    }

    private static AsyncProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));

        return new AsyncProvider(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(httpClient));
    }

    private static AsyncProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "async" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
