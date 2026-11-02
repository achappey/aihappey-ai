using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Cartesia;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cartesia;

public sealed class CartesiaProviderSpeechTests
{
    [Theory]
    [InlineData("tts/sonic-3.6/voice-id", true, false, false)]
    [InlineData("tts/sonic-latest/voice-id", true, false, false)]
    [InlineData("transcription/realtime/auto/ink-2", false, true, false)]
    [InlineData("agents/agent-id", false, false, true)]
    public async Task Realtime_tokens_grant_only_the_selected_capability(string model, bool tts, bool stt, bool agent)
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/access-token", request.RequestUri!.AbsolutePath);
            Assert.Equal("2026-08-14", request.Headers.GetValues("Cartesia-Version").Single());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var grants = body.RootElement.GetProperty("grants");
            Assert.Equal(tts, grants.GetProperty("tts").GetBoolean());
            Assert.Equal(stt, grants.GetProperty("stt").GetBoolean());
            Assert.Equal(agent, grants.GetProperty("agent").GetBoolean());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"ephemeral-fixture\"}") };
        });
        var response = await provider.GetRealtimeToken(new RealtimeRequest { Model = model });
        Assert.Equal("ephemeral-fixture", response.Value);
        Assert.True(response.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal("2026-08-14", response.ProviderMetadata!["cartesia"].GetProperty("cartesia_version").GetString());
    }

    [Theory]
    [InlineData("tts/unknown/voice-id")]
    [InlineData("tts/sonic-3.6/")]
    [InlineData("tts/sonic-3.6/voice/extra")]
    [InlineData("transcription/realtime/auto/ink-whisper")]
    public async Task Invalid_realtime_models_never_request_a_token(string model)
    {
        var provider = CreateProvider(_ => throw new Exception("Invalid model reached the provider."));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(new RealtimeRequest { Model = model }));
    }

    [Fact]
    public async Task Dynamic_speech_models_keep_voice_tags_and_advertise_streaming_without_new_ids()
    {
        var provider = CreateProvider(request => new(HttpStatusCode.OK) { Content = new StringContent(
            request.RequestUri!.AbsolutePath == "/voices" ? "{\"data\":[{\"id\":\"voice-id\",\"language\":\"en\",\"gender\":\"female\"}],\"has_more\":false}" : "{\"data\":[]}") });
        var speech = (await provider.ListModels(default)).Where(model => model.Type == "speech").ToArray();
        Assert.Equal(4, speech.Length);
        Assert.All(speech, model => { Assert.Contains("real-time", model.Tags!); Assert.Contains("realtime-token", model.Tags!); Assert.Contains("voice", model.Tags!); Assert.Contains("female", model.Tags!); });
        Assert.Contains(speech, model => model.Id == "cartesia/tts/sonic-3.6/voice-id");
    }

    [Theory]
    [InlineData("pcm", "raw", "audio/pcm")]
    [InlineData("ogg", "wav", "audio/wav")]
    public async Task SpeechRequest_reports_the_format_sent_to_Cartesia(
        string outputFormat,
        string expectedContainer,
        string contentType)
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/tts/bytes", request.RequestUri?.AbsolutePath);
            Assert.Equal("2026-08-14", request.Headers.GetValues("Cartesia-Version").Single());
            capturedPayload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new(contentType);
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Text = "Hello from Cartesia.",
            OutputFormat = outputFormat
        });

        Assert.NotNull(capturedPayload);
        Assert.Equal(expectedContainer, capturedPayload.Value
            .GetProperty("output_format").GetProperty("container").GetString());
        Assert.Equal(4, capturedPayload.Value.EnumerateObject().Count());
        Assert.Equal(expectedContainer, response.Audio.Format);
        Assert.Equal(contentType, response.Audio.MimeType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.Equal("pcm_s16le", capturedPayload.Value.GetProperty("output_format").GetProperty("encoding").GetString());
        Assert.Equal(44100, capturedPayload.Value.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
    }

    [Fact]
    public async Task SpeechRequest_forwards_raw_provider_options_at_the_upstream_root_and_preserves_known_precedence()
    {
        JsonElement? capturedPayload = null;
        using var metadataDocument = JsonDocument.Parse("""
            {
              "apiVersion": "2026-09-10",
              "language": "de",
              "container": "wav",
              "sampleRate": 22050,
              "bitRate": 96000,
              "encoding": "pcm_f32le",
              "speed": 0.8,
              "volume": 1.25,
              "emotion": "curious",
              "voice": {"future_voice": {"enabled": true}, "id": "metadata-voice"},
              "output_format": {"future_format": {"preserved": true}, "container": "raw"},
              "generation_config": {"future_config": [3, {"flag": true}], "speed": 0.3},
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var metadata = metadataDocument.RootElement.Clone();
        var request = new SpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Text = "Hello from Cartesia.",
            Language = "fr",
            OutputFormat = "mp3",
            Speed = 1.2f,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cartesia"] = metadata,
                ["another-provider"] = JsonDocument.Parse("""{"must_not_leak":true}""").RootElement.Clone()
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        var provider = CreateProvider(message =>
        {
            Assert.Equal("2026-09-10", message.Headers.GetValues("Cartesia-Version").Single());
            capturedPayload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });

        await provider.SpeechRequest(request);

        Assert.True(JsonElement.DeepEquals(metadata, serializedClientRequest
            .GetProperty("providerOptions").GetProperty("cartesia")));
        Assert.True(capturedPayload.HasValue);
        var root = capturedPayload.Value;
        Assert.Equal("sonic-3.6", root.GetProperty("model_id").GetString());
        Assert.Equal("Hello from Cartesia.", root.GetProperty("transcript").GetString());
        Assert.Equal("voice-id", root.GetProperty("voice").GetProperty("id").GetString());
        Assert.True(root.GetProperty("voice").GetProperty("future_voice").GetProperty("enabled").GetBoolean());
        Assert.Equal("fr", root.GetProperty("language").GetString());
        var outputFormat = root.GetProperty("output_format");
        Assert.Equal("mp3", outputFormat.GetProperty("container").GetString());
        Assert.Equal(22050, outputFormat.GetProperty("sample_rate").GetInt32());
        Assert.Equal(96000, outputFormat.GetProperty("bit_rate").GetInt32());
        Assert.True(outputFormat.GetProperty("future_format").GetProperty("preserved").GetBoolean());
        var generationConfig = root.GetProperty("generation_config");
        Assert.Equal(1.2, generationConfig.GetProperty("speed").GetDouble(), 4);
        Assert.Equal(1.25, generationConfig.GetProperty("volume").GetDouble());
        Assert.Equal("curious", generationConfig.GetProperty("emotion").GetString());
        Assert.Equal(3, generationConfig.GetProperty("future_config")[0].GetInt32());
        Assert.True(generationConfig.GetProperty("future_config")[1].GetProperty("flag").GetBoolean());
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"nested":[{"value":1},[true,null,"text"]]}""").RootElement,
            root.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""[1,{"more":[false,2.5]},[]]""").RootElement,
            root.GetProperty("future_array")));
        Assert.Equal("unchanged", root.GetProperty("future_string").GetString());
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("12345678901234567890.123456789").RootElement,
            root.GetProperty("future_number")));
        Assert.False(root.GetProperty("future_boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("apiVersion", out _));
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("cartesia", out _));
        Assert.False(root.TryGetProperty("another-provider", out _));
    }

    [Fact]
    public async Task SpeechRequest_omits_null_mapped_options_but_preserves_unknown_nulls()
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(message =>
        {
            capturedPayload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Text = "Hello from Cartesia.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cartesia"] = JsonDocument.Parse("""
                    {
                      "apiVersion": null,
                      "language": null,
                      "container": null,
                      "sampleRate": null,
                      "bitRate": null,
                      "encoding": null,
                      "speed": null,
                      "volume": null,
                      "emotion": null,
                      "future_null": null
                    }
                    """).RootElement.Clone()
            }
        });

        Assert.True(capturedPayload.HasValue);
        var root = capturedPayload.Value;
        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("language", out _));
        Assert.False(root.TryGetProperty("generation_config", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        var outputFormat = root.GetProperty("output_format");
        Assert.Equal("wav", outputFormat.GetProperty("container").GetString());
        Assert.Equal("pcm_s16le", outputFormat.GetProperty("encoding").GetString());
        Assert.Equal(44100, outputFormat.GetProperty("sample_rate").GetInt32());
    }

    [Fact]
    public async Task OpenAI_speech_merges_additional_properties_into_the_upstream_root()
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(message =>
        {
            Assert.Equal("/tts/bytes", message.RequestUri?.AbsolutePath);
            capturedPayload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });

        await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Input = "Hello from OpenAI compatibility.",
            ResponseFormat = "mp3",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["future_object"] = JsonDocument.Parse("""{"nested":[1,true,null,{"leaf":"kept"}]}""").RootElement.Clone(),
                ["future_array"] = JsonDocument.Parse("""[1,{"next":[false,2.5]}]""").RootElement.Clone(),
                ["future_null"] = JsonDocument.Parse("null").RootElement.Clone(),
                ["output_format"] = JsonDocument.Parse("""{"future_format":true}""").RootElement.Clone()
            }
        });

        Assert.True(capturedPayload.HasValue);
        var root = capturedPayload.Value;
        Assert.Equal("sonic-3.6", root.GetProperty("model_id").GetString());
        Assert.Equal("Hello from OpenAI compatibility.", root.GetProperty("transcript").GetString());
        Assert.Equal("mp3", root.GetProperty("output_format").GetProperty("container").GetString());
        Assert.True(root.GetProperty("output_format").GetProperty("future_format").GetBoolean());
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"nested":[1,true,null,{"leaf":"kept"}]}""").RootElement,
            root.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""[1,{"next":[false,2.5]}]""").RootElement,
            root.GetProperty("future_array")));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("additionalProperties", out _));
        Assert.False(root.TryGetProperty("providerOptions", out _));
    }

    [Fact]
    public async Task OpenAI_streaming_speech_merges_additional_properties_into_the_upstream_root()
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(message =>
        {
            Assert.Equal("/tts/sse", message.RequestUri?.AbsolutePath);
            capturedPayload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"type\":\"done\"}\n\n")
            };
        });

        await foreach (var _ in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Input = "Streamed OpenAI-compatible speech.",
            StreamFormat = "sse",
            ResponseFormat = "pcm",
            Speed = 1.2f,
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["future_object"] = JsonDocument.Parse("""{"enabled":true}""").RootElement.Clone(),
                ["generation_config"] = JsonDocument.Parse("""{"future_generation_option":[1,null]}""").RootElement.Clone()
            }
        }))
        {
        }

        Assert.True(capturedPayload.HasValue);
        var root = capturedPayload.Value;
        Assert.Equal("sonic-3.6", root.GetProperty("model_id").GetString());
        Assert.Equal("Streamed OpenAI-compatible speech.", root.GetProperty("transcript").GetString());
        Assert.True(root.GetProperty("future_object").GetProperty("enabled").GetBoolean());
        var generationConfig = root.GetProperty("generation_config");
        Assert.Equal(1.2, generationConfig.GetProperty("speed").GetDouble(), 4);
        Assert.Equal(1, generationConfig.GetProperty("future_generation_option")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, generationConfig.GetProperty("future_generation_option")[1].ValueKind);
        Assert.False(root.TryGetProperty("additionalProperties", out _));
        Assert.False(root.TryGetProperty("providerOptions", out _));
    }

    [Fact]
    public async Task SpeechRequest_preserves_invalid_known_option_and_upstream_error_behavior()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        var invalidMetadata = new SpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Text = "Hello from Cartesia.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cartesia"] = JsonDocument.Parse("""{"sampleRate":"fast"}""").RootElement.Clone()
            }
        };
        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(invalidMetadata));

        var failingProvider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingProvider.SpeechRequest(new SpeechRequest
            {
                Model = "tts/sonic-3.6/voice-id",
                Text = "Hello from Cartesia."
            }));
        Assert.Contains("Cartesia TTS failed (400): upstream failure", error.Message);
    }

    private static CartesiaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new CartesiaProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
