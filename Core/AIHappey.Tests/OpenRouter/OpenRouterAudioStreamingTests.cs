using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenRouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenRouter;

public sealed class OpenRouterAudioStreamingTests
{
    [Fact]
    public async Task Speech_streaming_yields_audio_before_response_body_completes()
    {
        var stream = new GatedStream([1, 2, 3, 4], [5, 6]);
        var provider = CreateProvider(_ => Task.FromResult(AudioResponse(stream, "audio/mpeg")));

        await using var parts = provider.SpeechStreamingAsync(CreateSpeechRequest(format: "mp3")).GetAsyncEnumerator();

        Assert.True(await parts.MoveNextAsync());
        Assert.IsType<SpeechStreamStartPart>(parts.Current);
        Assert.True(await parts.MoveNextAsync());
        Assert.IsType<SpeechResponseMetadataPart>(parts.Current);
        Assert.True(await parts.MoveNextAsync());
        var first = Assert.IsType<SpeechAudioPart>(parts.Current);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), first.Audio);
        Assert.Equal("audio/mpeg", first.AudioFormat.Type);

        var next = parts.MoveNextAsync().AsTask();
        await stream.SecondChunkRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(next.IsCompleted);

        stream.Gate.SetResult();
        Assert.True(await next);
        Assert.Equal(Convert.ToBase64String([5, 6]), Assert.IsType<SpeechAudioPart>(parts.Current).Audio);
        Assert.True(await parts.MoveNextAsync());
        Assert.IsType<SpeechFinishPart>(parts.Current);
        Assert.False(await parts.MoveNextAsync());
    }

    [Fact]
    public async Task Speech_streaming_sends_payload_and_reports_metadata()
    {
        string? body = null;
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            var response = AudioResponse(new MemoryStream([1, 2, 3, 4, 5]), "audio/pcm;rate=24000;channels=1");
            response.Headers.Add("X-Generation-Id", "gen-tts-123");
            return response;
        });

        var request = CreateSpeechRequest(format: "pcm");
        request.Text =
        [
            new StreamingSpeechTextPart { Type = "text", Text = "Hello " },
            new StreamingSpeechTextPart { Type = "text", Text = "world", Voice = "nova" }
        ];
        request.Voice = null;
        request.Language = "en";
        request.Speed = 1.25;
        request.IncludeRawChunks = true;
        request.ProviderOptions = new Dictionary<string, JsonElement>
        {
            ["openrouter"] = JsonSerializer.SerializeToElement(new { provider = new { options = new { openai = new { instructions = "calm" } } } })
        };

        var parts = await CollectAsync(provider.SpeechStreamingAsync(request));

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://openrouter.ai/api/v1/audio/speech", captured.RequestUri!.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        using (var json = JsonDocument.Parse(body!))
        {
            var root = json.RootElement;
            Assert.Equal("openai/gpt-4o-mini-tts", root.GetProperty("model").GetString());
            Assert.Equal("Hello world", root.GetProperty("input").GetString());
            Assert.Equal("nova", root.GetProperty("voice").GetString());
            Assert.Equal("pcm", root.GetProperty("response_format").GetString());
            Assert.Equal(1.25, root.GetProperty("speed").GetDouble());
            Assert.Equal("calm", root.GetProperty("provider").GetProperty("options").GetProperty("openai").GetProperty("instructions").GetString());
        }

        var start = Assert.IsType<SpeechStreamStartPart>(parts[0]);
        Assert.Single(start.Warnings);
        var metadata = Assert.IsType<SpeechResponseMetadataPart>(parts[1]);
        Assert.Equal("openrouter/openai/gpt-4o-mini-tts", metadata.ModelId);
        Assert.Equal("gen-tts-123", metadata.Headers!["X-Generation-Id"]);

        var audio = parts.OfType<SpeechAudioPart>().ToList();
        Assert.Equal(2, audio.Count);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), audio[0].Audio);
        Assert.Equal(Convert.ToBase64String([5]), audio[1].Audio);
        Assert.All(audio, part =>
        {
            Assert.Equal("audio/pcm", part.AudioFormat.Type);
            Assert.Equal(24000, part.AudioFormat.Rate);
        });
        Assert.Equal(2, parts.OfType<SpeechRawPart>().Count());

        var finish = Assert.IsType<SpeechFinishPart>(parts[^1]);
        Assert.Equal(11, finish.Usage!.InputCharacters);
        var providerMetadata = finish.ProviderMetadata!["openrouter"];
        Assert.Equal("gen-tts-123", providerMetadata.GetProperty("generationId").GetString());
        Assert.Equal(5, providerMetadata.GetProperty("audioBytes").GetInt64());
    }

    [Fact]
    public async Task Speech_streaming_throws_on_error_status_without_parts()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"invalid voice"}}""", Encoding.UTF8, "application/json")
        }));

        var parts = new List<StreamingSpeechPart>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var part in provider.SpeechStreamingAsync(CreateSpeechRequest()))
                parts.Add(part);
        });

        Assert.Contains("(400)", ex.Message);
        Assert.Contains("invalid voice", ex.Message);
        Assert.Empty(parts);
    }

    [Fact]
    public async Task Speech_streaming_rejects_multiple_voices_and_unsupported_formats()
    {
        var called = false;
        var provider = CreateProvider(_ =>
        {
            called = true;
            return Task.FromResult(AudioResponse(new MemoryStream([1]), "audio/mpeg"));
        });

        var multiVoice = CreateSpeechRequest();
        multiVoice.Text =
        [
            new StreamingSpeechTextPart { Type = "text", Text = "a", Voice = "alloy" },
            new StreamingSpeechTextPart { Type = "text", Text = "b", Voice = "nova" }
        ];

        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(provider.SpeechStreamingAsync(multiVoice)));
        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(provider.SpeechStreamingAsync(CreateSpeechRequest(format: "wav"))));
        Assert.False(called);
    }

    [Fact]
    public async Task Speech_streaming_honors_cancellation_while_reading_audio()
    {
        var stream = new GatedStream([1, 2, 3, 4], [5, 6]);
        var provider = CreateProvider(_ => Task.FromResult(AudioResponse(stream, "audio/mpeg")));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var part in provider.SpeechStreamingAsync(CreateSpeechRequest(format: "mp3"), cts.Token))
            {
                if (part is SpeechAudioPart)
                    cts.Cancel();
            }
        });
    }

    [Fact]
    public async Task Transcription_streaming_mimics_stream_from_verbose_response()
    {
        string? body = null;
        var provider = CreateProvider(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "text": "Hello there. Hi!",
                      "language": "en",
                      "duration": 3.1,
                      "segments": [
                        { "id": 0, "start": 0.0, "end": 1.2, "text": "Hello there." },
                        { "id": 1, "start": 1.5, "end": 3.1, "text": "Hi!" }
                      ],
                      "usage": { "seconds": 3.1, "cost": 0.0002 }
                    }
                    """, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("X-Generation-Id", "gen-stt-1");
            return response;
        });

        var parts = await CollectAsync(provider.TranscriptionStreamingAsync(new StreamingTranscriptionRequest
        {
            Model = "openai/whisper-1",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            InputAudioFormat = new AudioFormat { Type = "mp3" },
            IncludeRawChunks = true,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["openrouter"] = JsonSerializer.SerializeToElement(new { response_format = "verbose_json" })
            }
        }));

        using (var json = JsonDocument.Parse(body!))
        {
            Assert.Equal("mp3", json.RootElement.GetProperty("input_audio").GetProperty("format").GetString());
            Assert.Equal("verbose_json", json.RootElement.GetProperty("response_format").GetString());
        }

        Assert.IsType<TranscriptionStreamStartPart>(parts[0]);
        var metadata = Assert.IsType<TranscriptionResponseMetadataPart>(parts[1]);
        Assert.Equal("openrouter/openai/whisper-1", metadata.ModelId);
        Assert.IsType<TranscriptionRawPart>(parts[2]);

        var finals = parts.OfType<TranscriptionFinalPart>().ToList();
        Assert.Equal(2, finals.Count);
        Assert.Equal("Hi!", finals[1].Text);
        Assert.Equal(1.5, finals[1].StartSecond!.Value, 3);
        Assert.Equal(3.1, finals[1].EndSecond!.Value, 3);

        Assert.Equal("Hello there. Hi!", Assert.Single(parts.OfType<TranscriptionDeltaPart>()).Delta);

        var finish = Assert.IsType<TranscriptionFinishPart>(parts[^1]);
        Assert.Equal("Hello there. Hi!", finish.Text);
        Assert.Equal("en", finish.Language);
        Assert.Equal(3.1, finish.DurationInSeconds!.Value, 3);
        Assert.Equal(2, finish.Segments.Count());
        Assert.Equal(0.0002m, finish.ProviderMetadata!["gateway"].GetProperty("cost").GetDecimal());
        Assert.Equal("gen-stt-1", finish.ProviderMetadata["openrouter"].GetProperty("generationId").GetString());
        Assert.Equal(3.1, finish.ProviderMetadata["openrouter"].GetProperty("usage").GetProperty("seconds").GetDouble(), 3);
    }

    [Fact]
    public async Task Transcription_streaming_rejects_raw_pcm_and_surfaces_errors()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"bad audio"}""", Encoding.UTF8, "application/json")
        }));

        var request = new StreamingTranscriptionRequest
        {
            Model = "openai/whisper-1",
            Audio = Convert.ToBase64String([1, 2, 3]),
            InputAudioFormat = new AudioFormat { Type = "audio/pcm", Rate = 16000 }
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(provider.TranscriptionStreamingAsync(request)));

        request.InputAudioFormat = new AudioFormat { Type = "audio/wav" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(provider.TranscriptionStreamingAsync(request)));
        Assert.Contains("bad audio", ex.Message);
    }

    [Fact]
    public async Task Model_discovery_maps_speech_and_transcription_models()
    {
        var requested = new List<string>();
        var provider = CreateProvider(request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            var json = request.RequestUri.Query.Contains("output_modalities=all")
                ? """
                  { "data": [
                    { "id": "openai/gpt-4o", "name": "GPT-4o", "architecture": { "input_modalities": ["text"], "output_modalities": ["text"] }, "pricing": { "prompt": "0.0000025", "completion": "0.00001" } },
                    { "id": "openai/gpt-audio", "name": "GPT Audio", "architecture": { "input_modalities": ["text", "audio"], "output_modalities": ["text", "audio"] }, "pricing": { "prompt": "0.0000025", "completion": "0.00001" } }
                  ] }
                  """
                : """
                  { "data": [
                    { "id": "hexgrad/kokoro-82m", "name": "Kokoro", "architecture": { "input_modalities": ["text"], "output_modalities": ["speech"] }, "supported_voices": ["af_heart", "am_adam"], "pricing": { "prompt": "0.00000065", "completion": "0" } },
                    { "id": "openai/gpt-4o-mini-tts", "name": "GPT-4o mini TTS", "architecture": { "input_modalities": ["text"], "output_modalities": ["speech"] }, "supported_voices": ["alloy"], "pricing": { "prompt": "0.0000006", "completion": "0.000012" } },
                    { "id": "openai/whisper-1", "name": "Whisper", "architecture": { "input_modalities": ["audio"], "output_modalities": ["transcription"] }, "pricing": { "prompt": "0.0001", "completion": "0" } }
                  ] }
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Contains("/api/v1/models?output_modalities=all", requested);
        Assert.Contains("/api/v1/models?output_modalities=speech,transcription", requested);
        Assert.DoesNotContain(models, m => m.Id == "openrouter/openai/gpt-audio" && m.Type == "speech");
        Assert.Contains(models, m => m.Id == "openrouter/openai/gpt-audio" && m.Type == "language");

        var kokoro = Assert.Single(models, m => m.Id == "openrouter/hexgrad/kokoro-82m");
        Assert.Equal("speech", kokoro.Type);
        Assert.Null(kokoro.Pricing);
        Assert.Contains("voice:af_heart", kokoro.Tags!);
        Assert.Contains("voice:am_adam", kokoro.Tags!);
        Assert.Contains("output-format:mp3", kokoro.Tags!);
        Assert.Contains("output-format:pcm", kokoro.Tags!);
        Assert.Contains("price-per-input-character:0.00000065", kokoro.Tags!);

        var tts = Assert.Single(models, m => m.Id == "openrouter/openai/gpt-4o-mini-tts");
        Assert.NotNull(tts.Pricing);
        Assert.Equal(0.000012m, tts.Pricing!.Output);
        Assert.Contains("voice:alloy", tts.Tags!);

        var whisper = Assert.Single(models, m => m.Id == "openrouter/openai/whisper-1");
        Assert.Equal("transcription", whisper.Type);
        Assert.Null(whisper.Pricing);
        Assert.Contains("input-format:webm", whisper.Tags!);
        Assert.Contains("price-per-input-second:0.0001", whisper.Tags!);

        var gpt4o = Assert.Single(models, m => m.Id == "openrouter/openai/gpt-4o");
        Assert.Null(gpt4o.Tags);
        Assert.Equal(0.0000025m, gpt4o.Pricing!.Input);
    }

    [Fact]
    public async Task Model_discovery_tolerates_failed_audio_catalog_query()
    {
        var provider = CreateProvider(request => Task.FromResult(request.RequestUri!.Query.Contains("output_modalities=all")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": [ { "id": "openai/gpt-4o", "architecture": { "input_modalities": ["text"], "output_modalities": ["text"] } } ] }""", Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(HttpStatusCode.BadRequest)));

        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("openrouter/openai/gpt-4o", model.Id);
    }

    private static StreamingSpeechRequest CreateSpeechRequest(string? format = null)
        => new()
        {
            Model = "openai/gpt-4o-mini-tts",
            Voice = "alloy",
            Text = [new StreamingSpeechTextPart { Type = "text", Text = "Hello" }],
            OutputFormat = format is null ? null : new AudioFormat { Type = format }
        };

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var parts = new List<T>();
        await foreach (var part in source)
            parts.Add(part);
        return parts;
    }

    private static HttpResponseMessage AudioResponse(Stream stream, string contentType)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static OpenRouterProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://openrouter.ai/api/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new OpenRouterProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private sealed class GatedStream(byte[] first, byte[] second) : Stream
    {
        private int _reads;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondChunkRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            switch (_reads++)
            {
                case 0:
                    first.CopyTo(buffer);
                    return first.Length;
                case 1:
                    SecondChunkRequested.TrySetResult();
                    await Gate.Task.WaitAsync(cancellationToken);
                    second.CopyTo(buffer);
                    return second.Length;
                default:
                    return 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await responder(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
