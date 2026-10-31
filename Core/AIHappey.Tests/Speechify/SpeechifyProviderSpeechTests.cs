using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Speechify;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Speechify;

public sealed class SpeechifyProviderSpeechTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsync_uses_native_speech_request_mapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        audio_data = Convert.ToBase64String(Encoding.UTF8.GetBytes("mp3-audio")),
                        audio_format = "mp3",
                        billable_characters_count = 10,
                        speech_marks = new
                        {
                            chunks = Array.Empty<object>(),
                            end = 1,
                            end_time = 1,
                            start = 0,
                            start_time = 0,
                            type = "sentence"
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "simba-3.2/geffen_32",
            Input = "Hello from Speechify OpenAI compatibility!",
            ResponseFormat = "mp3",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["future_object"] = JsonSerializer.SerializeToElement(new
                {
                    nested = new[] { 1, 2 },
                    enabled = true
                }),
                ["future_array"] = JsonSerializer.SerializeToElement(new object?[] { "value", null, 3 }),
                ["future_number"] = JsonSerializer.SerializeToElement(4.5),
                ["future_null"] = JsonSerializer.SerializeToElement<string?>(null)
            }
        });

        Assert.Equal(Encoding.UTF8.GetBytes("mp3-audio"), audio);
        Assert.Equal("audio/mpeg", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/audio/speech", capturedRequest.RequestUri?.AbsolutePath);

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("simba-3.2", root.GetProperty("model").GetString());
        Assert.Equal("geffen_32", root.GetProperty("voice_id").GetString());
        Assert.Equal("Hello from Speechify OpenAI compatibility!", root.GetProperty("input").GetString());
        Assert.Equal("mp3", root.GetProperty("audio_format").GetString());
        Assert.Equal(2, root.GetProperty("future_object").GetProperty("nested").GetArrayLength());
        Assert.True(root.GetProperty("future_object").GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_array")[1].ValueKind);
        Assert.Equal(4.5, root.GetProperty("future_number").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public async Task SpeechRequest_forwards_raw_speechify_provider_options_at_upstream_root()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        audio_data = Convert.ToBase64String(Encoding.UTF8.GetBytes("wav-audio")),
                        audio_format = "wav"
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var providerOptions = JsonSerializer.SerializeToElement(new
        {
            voice_id = "metadata-voice",
            audio_format = "mp3",
            language = "fr-FR",
            options = new
            {
                text_normalization = false,
                future_option = new[] { "one", "two" }
            },
            future_object = new { value = 42 },
            future_array = new object?[] { true, null, "three" },
            future_primitive = 17,
            future_null = (string?)null
        });
        var request = new SpeechRequest
        {
            Model = "simba-3.2",
            Text = "Hello from Vercel metadata!",
            Voice = "request-voice",
            OutputFormat = "wav",
            Language = "en-US",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["speechify"] = providerOptions,
                ["other"] = JsonSerializer.SerializeToElement(new { ignored = true })
            }
        };

        var response = await provider.SpeechRequest(request);

        Assert.NotNull(capturedRequest);
        using var document = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("simba-3.2", root.GetProperty("model").GetString());
        Assert.Equal("metadata-voice", root.GetProperty("voice_id").GetString());
        Assert.Equal("wav", root.GetProperty("audio_format").GetString());
        Assert.Equal("en-US", root.GetProperty("language").GetString());
        Assert.False(root.GetProperty("options").GetProperty("text_normalization").GetBoolean());
        Assert.Equal(2, root.GetProperty("options").GetProperty("future_option").GetArrayLength());
        Assert.Equal(42, root.GetProperty("future_object").GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_array")[1].ValueKind);
        Assert.Equal(17, root.GetProperty("future_primitive").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("speechify", out _));
        Assert.False(root.TryGetProperty("ignored", out _));
        Assert.Equal(
            JsonValueKind.Object,
            JsonSerializer.SerializeToElement(request)
                .GetProperty("providerOptions")
                .GetProperty("speechify")
                .ValueKind);
        Assert.Contains("speechify", response.ProviderMetadata!.Keys);
        Assert.True(request.ProviderOptions!["speechify"].TryGetProperty("future_null", out _));
    }

    [Fact]
    public async Task SpeechRequest_keeps_default_format_and_omits_missing_language()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        audio_data = Convert.ToBase64String(Encoding.UTF8.GetBytes("wav-audio")),
                        audio_format = "wav"
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "simba-3.2",
            Text = "Hello!",
            Voice = "geffen_32"
        });

        Assert.NotNull(capturedRequest);
        using var document = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("wav", root.GetProperty("audio_format").GetString());
        Assert.False(root.TryGetProperty("language", out _));
        Assert.False(root.TryGetProperty("options", out _));
    }

    [Fact]
    public async Task SpeechRequest_preserves_invalid_known_option_type_error()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));
        var request = new SpeechRequest
        {
            Model = "simba-3.2",
            Text = "Hello!",
            Voice = "geffen_32",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["speechify"] = JsonSerializer.SerializeToElement(new
                {
                    options = new { text_normalization = "false" }
                })
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(request));
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_posts_streaming_request_and_maps_raw_audio_to_openai_events()
    {
        HttpRequestMessage? capturedRequest = null;
        var audioChunk = Encoding.UTF8.GetBytes("streamed-audio");
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
            Model = "simba-3.2",
            Voice = "geffen_32",
            Input = "Hello streamed Speechify!",
            ResponseFormat = "mp3",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["future_array"] = JsonSerializer.SerializeToElement(new[] { "a", "b" }),
                ["future_null"] = JsonSerializer.SerializeToElement<string?>(null),
                ["model"] = JsonSerializer.SerializeToElement("overridden-model")
            }
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
        Assert.Equal("/v1/audio/stream", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Empty(capturedRequest.Headers.Accept);

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("simba-3.2", root.GetProperty("model").GetString());
        Assert.Equal("geffen_32", root.GetProperty("voice_id").GetString());
        Assert.Equal("Hello streamed Speechify!", root.GetProperty("input").GetString());
        Assert.Equal("mp3_24000_128", root.GetProperty("output_format").GetString());
        Assert.Equal(2, root.GetProperty("future_array").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_falls_back_to_non_streaming_for_wav_requests()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        audio_data = Convert.ToBase64String(Encoding.UTF8.GetBytes("wav-audio")),
                        audio_format = "wav",
                        billable_characters_count = 10,
                        speech_marks = new
                        {
                            chunks = Array.Empty<object>(),
                            end = 1,
                            end_time = 1,
                            start = 0,
                            start_time = 0,
                            type = "sentence"
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "simba-3.2/geffen_32",
                           Input = "Hello fallback Speechify!",
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
        Assert.Equal("/v1/audio/speech", capturedRequest!.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_throws_with_speechify_error_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid voice", Encoding.UTF8, "text/plain")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "simba-3.2",
                               Voice = "missing_voice",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Speechify streaming TTS failed (400): invalid voice", exception.Message);
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsync_requires_voice_for_base_model()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                           {
                               Model = "simba-3.2",
                               Input = "Hello!"
                           }))
            {
            }
        });

        Assert.Contains("Speechify requires a voice_id", exception.Message);
    }

    private static SpeechifyProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new SpeechifyProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
