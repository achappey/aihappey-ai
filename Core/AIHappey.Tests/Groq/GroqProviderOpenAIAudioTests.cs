using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Groq;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Groq;

public sealed class GroqProviderOpenAIAudioTests
{
    [Theory]
    [InlineData("wav", "audio/wav")]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("flac", "audio/flac")]
    [InlineData("mulaw", "audio/basic")]
    [InlineData("ogg", "audio/ogg")]
    public async Task Native_speech_request_maps_supported_formats_and_speed(
        string format,
        string mimeType)
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("generated audio"))
            };
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "playai-tts",
            Text = "Hello from Groq",
            Voice = "Fritz-PlayAI",
            OutputFormat = format,
            Speed = 1.25f
        });

        Assert.Equal(mimeType, response.Audio.MimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal("/openai/v1/audio/speech", capturedRequest!.RequestUri?.AbsolutePath);

        using var body = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        Assert.Equal(format, body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(1.25f, body.RootElement.GetProperty("speed").GetSingle());
    }

    [Fact]
    public async Task Speech_request_sends_OpenAI_payload_and_returns_audio_with_provider_mime_type()
    {
        HttpRequestMessage? capturedRequest = null;
        var expectedAudio = Encoding.UTF8.GetBytes("generated audio");
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedAudio)
                {
                    Headers = { ContentType = new("audio/wav") }
                }
            };
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "playai-tts",
            Input = "Hello from Groq",
            Voice = "Fritz-PlayAI",
            ResponseFormat = "wav",
            Speed = 1.25f
        });

        Assert.Equal(expectedAudio, audio);
        Assert.Equal("audio/wav", mimeType);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/openai/v1/audio/speech", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);

        using var body = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        Assert.Equal("playai-tts", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello from Groq", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("Fritz-PlayAI", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal("wav", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(1.25f, body.RootElement.GetProperty("speed").GetSingle());
        Assert.Equal("audio", body.RootElement.GetProperty("stream_format").GetString());
    }

    [Fact]
    public async Task Speech_stream_emulates_audio_delta_then_done()
    {
        var expectedAudio = Encoding.UTF8.GetBytes("generated audio");
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedAudio)
        });
        var events = new List<IAudioSpeechStreamEvent>();

        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "playai-tts",
                           Input = "Hello from Groq",
                           Voice = "Fritz-PlayAI",
                           ResponseFormat = "wav"
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first => Assert.Equal(
                Convert.ToBase64String(expectedAudio),
                Assert.IsType<AudioSpeechStreamDelta>(first).Audio),
            second => Assert.IsType<AudioSpeechStreamDone>(second));
    }

    [Fact]
    public async Task Transcription_request_sends_supported_multipart_fields_and_maps_verbose_json()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""
                {
                  "text": "hello world",
                  "language": "en",
                  "duration": 1.25,
                  "segments": [
                    { "id": 0, "seek": 0, "start": 0, "end": 1.25, "text": "hello world", "tokens": [], "temperature": 0, "avg_logprob": -0.1, "compression_ratio": 1, "no_speech_prob": 0 }
                  ]
                }
                """);
        });

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "whisper-large-v3",
            File = CreateAudioFile(Encoding.UTF8.GetBytes("fake audio")),
            Language = "en",
            Prompt = "product names",
            ResponseFormat = "verbose_json",
            Temperature = 0.2f,
            TimestampGranularities = ["segment"]
        });

        var verbose = Assert.IsType<OpenAITranscriptionVerboseResponse>(response);
        Assert.Equal("hello world", verbose.Text);
        Assert.Equal("en", verbose.Language);
        Assert.Equal(1.25, verbose.Duration);
        Assert.Single(verbose.Segments!);

        Assert.NotNull(capturedRequest);
        Assert.Equal("/openai/v1/audio/transcriptions", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("name=file", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("name=model", body);
        Assert.Contains("whisper-large-v3", body);
        Assert.Contains("name=language", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("verbose_json", body);
        Assert.Contains("name=temperature", body);
        Assert.Contains("0.2", body);
        Assert.Contains("timestamp_granularities[]", body);
        Assert.Contains("segment", body);
        Assert.DoesNotContain("name=stream", body);
    }

    [Fact]
    public async Task Native_transcription_request_accepts_audio_data_url_deserialized_as_JsonElement()
    {
        var expectedAudio = Encoding.UTF8.GetBytes("fake audio");
        byte[]? capturedAudio = null;
        var provider = CreateProvider(request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var file = Assert.Single(form, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "file");
            capturedAudio = file.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return JsonResponse("""{ "text": "hello world" }""");
        });

        var audioDataUrl = $"data:audio/wav;base64,{Convert.ToBase64String(expectedAudio)}";
        using var audioJson = JsonDocument.Parse(JsonSerializer.Serialize(new { audio = audioDataUrl }));
        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = audioJson.RootElement.GetProperty("audio"),
            MediaType = "audio/wav"
        });

        Assert.Equal("hello world", response.Text);
        Assert.Equal(expectedAudio, capturedAudio);
    }

    [Fact]
    public async Task Native_transcription_forwards_provider_options_without_losing_unknown_json()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var provider = CreateProvider(request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (!fields.TryGetValue(name, out var values))
                    fields[name] = values = [];
                values.Add(part.ReadAsStringAsync().GetAwaiter().GetResult());
            }

            return JsonResponse("""{ "text": "hello world" }""");
        });

        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = JsonSerializer.SerializeToElement("data:audio/wav;base64,YXVkaW8="),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["groq"] = ParseElement("""
                    {
                      "language": "en",
                      "prompt": "product names",
                      "temperature": 0.25,
                      "timestamp_granularities": ["segment", "word"],
                      "response_format": "json",
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null,
                      "model": "not-the-request-model"
                    }
                    """),
                ["another-provider"] = JsonSerializer.SerializeToElement(new { secret = "not forwarded" })
            }
        });

        Assert.Equal("whisper-large-v3", Assert.Single(fields["model"]));
        Assert.Equal("en", Assert.Single(fields["language"]));
        Assert.Equal("product names", Assert.Single(fields["prompt"]));
        Assert.Equal("0.25", Assert.Single(fields["temperature"]));
        Assert.Equal(new[] { "segment", "word" }, fields["timestamp_granularities[]"]);
        Assert.Equal("verbose_json", Assert.Single(fields["response_format"]));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_object"])),
            ParseElement("""{"nested":[{"value":1},[true,null,"text"]]}""")));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_array"])),
            ParseElement("""[1,{"more":[false,2.5]},[]]""")));
        Assert.Equal("unchanged", Assert.Single(fields["future_string"]));
        Assert.Equal("12345678901234567890.123456789", Assert.Single(fields["future_number"]));
        Assert.Equal("false", Assert.Single(fields["future_boolean"]));
        Assert.Equal("null", Assert.Single(fields["future_null"]));
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("secret", fields.Keys);
    }

    [Fact]
    public async Task Native_transcription_omits_empty_metadata_and_preserves_known_validation()
    {
        var fieldNames = new List<string>();
        var provider = CreateProvider(request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            fieldNames.AddRange(form.Select(part => part.Headers.ContentDisposition!.Name!.Trim('"')));
            return JsonResponse("""{ "text": "hello world" }""");
        });

        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = JsonSerializer.SerializeToElement("data:audio/wav;base64,YXVkaW8="),
            MediaType = "audio/wav"
        });

        Assert.Equal(new[] { "file", "model" }, fieldNames);

        var invalidProvider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = JsonSerializer.SerializeToElement("data:audio/wav;base64,YXVkaW8="),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["groq"] = JsonSerializer.SerializeToElement(new { temperature = "invalid" })
            }
        }));
    }

    [Fact]
    public async Task Transcription_stream_emulates_text_delta_then_done()
    {
        var provider = CreateProvider(_ => JsonResponse("""{ "text": "hello world" }"""));
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "whisper-large-v3-turbo",
                           File = CreateAudioFile(Encoding.UTF8.GetBytes("fake audio"))
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first => Assert.Equal("hello world", Assert.IsType<OpenAITranscriptionTextDelta>(first).Delta),
            second => Assert.Equal("hello world", Assert.IsType<OpenAITranscriptionTextDone>(second).Text));
    }

    [Fact]
    public async Task Provider_errors_include_status_and_response_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unsupported voice", Encoding.UTF8, "application/json")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
            {
                Model = "playai-tts",
                Input = "Hello",
                Voice = "missing"
            }));

        Assert.Contains("400", error.Message);
        Assert.Contains("unsupported voice", error.Message);
    }

    private static IFormFile CreateAudioFile(byte[] audio)
        => new FormFile(new MemoryStream(audio, writable: false), 0, audio.Length, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static GroqProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var factory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new GroqProvider(new StaticApiKeyResolver(), cache, factory);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            clone.Content = new ByteArrayContent(content);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

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
