using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Google;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleSpeechStreamingTests
{
    private const string Model = "gemini-3.1-flash-tts-preview";
    private const string Completed = "event: interaction.completed\ndata: {\"event_type\":\"interaction.completed\",\"interaction\":{\"status\":\"completed\"}}\n\n";
    private static readonly string Audio = Frame("step.delta", new { index = 0, delta = new { type = "audio", data = "AQID" } });

    [Fact]
    public async Task Vercel_stream_preserves_split_words_voice_shortcuts_headers_and_reported_usage()
    {
        var usage = Json("""{"total_input_tokens":11,"total_output_tokens":22,"total_tokens":33,"input_characters":6,"output_tokens_by_modality":[{"modality":"audio","tokens":22}]}""");
        var body = Frame("step.delta", new { index = 7, delta = new { type = "audio", data = "AQID", mime_type = "audio/L16;rate=16000" } })
            + Frame("interaction.completed", new { interaction = new { status = "completed" }, metadata = new { total_usage = usage } });
        var handler = new RecordingHandler(body);
        var request = Request();
        request.Model = "google/" + Model + "/Puck";
        request.Text = [new() { Text = "hel" }, new() { Text = "lo!" }];
        request.Voice = "Kore";
        request.Speed = 1.1;
        request.Language = "en";
        request.Instructions = "cheerful";

        var events = await Collect(CreateProvider(handler).SpeechStreamingAsync(request));

        var start = Assert.IsType<SpeechStreamStartPart>(events[0]);
        Assert.Equal(3, start.Warnings.Count());
        Assert.DoesNotContain(start.Warnings, warning => JsonSerializer.SerializeToElement(warning).GetProperty("feature").GetString() == "instructions");
        var metadata = Assert.IsType<SpeechResponseMetadataPart>(events[1]);
        Assert.Equal("google/" + Model, metadata.ModelId);
        Assert.NotNull(metadata.Timestamp);
        Assert.Equal("request-123", metadata.Headers!["x-request-id"]);
        var audio = Assert.IsType<SpeechAudioPart>(events[2]);
        Assert.Equal("AQID", audio.Audio);
        Assert.Equal("7", audio.Id);
        Assert.Equal("audio/L16", audio.AudioFormat.Type);
        Assert.Equal(16000, audio.AudioFormat.Rate);
        Assert.True(audio.ProviderMetadata!.ContainsKey("google"));
        var finish = Assert.IsType<SpeechFinishPart>(events[3]);
        Assert.Equal(11, finish.Usage!.InputTokens);
        Assert.Equal(22, finish.Usage.OutputTokens);
        Assert.Equal(6, finish.Usage.InputCharacters);
        Assert.True(finish.Usage.Raw!.ContainsKey("output_tokens_by_modality"));
        Assert.True(JsonElement.DeepEquals(usage, finish.ProviderMetadata!["google"].GetProperty("usage")));
        Assert.DoesNotContain(events, e => e is SpeechRawPart);

        var payload = handler.Payload;
        var input = Assert.Single(payload.GetProperty("input").EnumerateArray());
        Assert.Equal("user_input", input.GetProperty("type").GetString());
        var content = Assert.Single(input.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("hello!", content.GetProperty("text").GetString());
        var annotation = Assert.Single(content.GetProperty("annotations").EnumerateArray());
        Assert.Equal("speech_metadata", annotation.GetProperty("type").GetString());
        Assert.Equal("cheerful", annotation.GetProperty("style").GetString());
        Assert.Equal(Model, payload.GetProperty("model").GetString());
        Assert.Equal("Puck", payload.GetProperty("generation_config").GetProperty("speech_config")[0].GetProperty("voice").GetString());
        Assert.True(payload.GetProperty("stream").GetBoolean());
        Assert.Equal("/v1beta/interactions", handler.Path);
        Assert.Equal("test-key", handler.Headers["x-goog-api-key"]);
        Assert.Equal("text/event-stream", handler.Headers["Accept"]);
        Assert.Equal("2026-05-20", handler.Headers["Api-Revision"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("warm and enthusiastic")]
    public async Task Vercel_unary_supports_instructions_without_unsupported_warnings(string? instructions)
    {
        var handler = new RecordingHandler("""{"output_audio":{"data":"AQID","mime_type":"audio/wav"}}""", mediaType: "application/json");
        var response = await CreateProvider(handler).SpeechRequest(new SpeechRequest
        {
            Model = "gemini-3.8-flash-tts",
            Text = "Hello <laugh>",
            Instructions = instructions
        });

        Assert.Empty(response.Warnings);
        Assert.Equal("AQID", response.Audio.Base64);
        var input = handler.Payload.GetProperty("input");
        if (string.IsNullOrWhiteSpace(instructions))
            Assert.Equal("Hello <laugh>", input.GetString());
        else
        {
            var content = input[0].GetProperty("content")[0];
            Assert.Equal("Hello <laugh>", content.GetProperty("text").GetString());
            Assert.Equal(instructions, content.GetProperty("annotations")[0].GetProperty("style").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vercel_stream_preserves_native_part_or_request_input_and_warns_once(bool partLevel)
    {
        var handler = new RecordingHandler(Audio + Completed);
        var request = Request();
        request.Instructions = "cheerful";
        request.Text = [new() { Text = "hel" }, new() { Text = "lo" }];
        var native = Options(Json("""{"input":"native transcript","future_option":{"enabled":true}}"""));
        request.ProviderOptions = partLevel ? Options(Json("""{"input":"request transcript"}""")) : native;
        if (partLevel)
            foreach (var part in request.Text)
                part.ProviderOptions = native;

        var events = await Collect(CreateProvider(handler).SpeechStreamingAsync(request));

        Assert.Equal("native transcript", handler.Payload.GetProperty("input").GetString());
        Assert.True(handler.Payload.GetProperty("future_option").GetProperty("enabled").GetBoolean());
        var warning = JsonSerializer.SerializeToElement(Assert.Single(Assert.IsType<SpeechStreamStartPart>(events[0]).Warnings));
        Assert.Equal("ignored", warning.GetProperty("type").GetString());
        Assert.Equal("instructions", warning.GetProperty("feature").GetString());
        Assert.Equal("providerOptions.google.input takes precedence", warning.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Vercel_unary_preserves_native_input_and_returns_ignored_instructions_warning()
    {
        var nativeInput = Json("""[{"type":"user_input","content":[{"type":"text","text":"Hello","annotations":[{"type":"speech_metadata","style":"friendly","speaker":"Joe"}]}]}]""");
        var handler = new RecordingHandler("""{"output_audio":{"data":"AQID","mime_type":"audio/wav"}}""", mediaType: "application/json");
        var response = await CreateProvider(handler).SpeechRequest(new SpeechRequest
        {
            Model = "gemini-3.8-flash-tts",
            Text = "generic transcript",
            Instructions = "generic style",
            ProviderOptions = Options(JsonSerializer.SerializeToElement(new { input = nativeInput }))
        });

        Assert.True(JsonElement.DeepEquals(nativeInput, handler.Payload.GetProperty("input")));
        var warning = JsonSerializer.SerializeToElement(Assert.Single(response.Warnings));
        Assert.Equal("ignored", warning.GetProperty("type").GetString());
        Assert.Equal("instructions", warning.GetProperty("feature").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Both_adapters_preserve_native_multi_speaker_input_and_unknown_fields(bool openAI, bool topLevelConfig)
    {
        var native = Json("""
            {
              "input":[{"type":"user_input","content":[
                {"type":"text","text":"Hello Jane!","annotations":[{"type":"speech_metadata","speaker":"Joe","style":"friendly"}]},
                {"type":"text","text":"Hello Joe!","annotations":[{"type":"speech_metadata","speaker":"Jane"}]}]}],
              "generation_config":{"speech_config":{"speakers":[{"speaker":"Joe","voice":"Kore"},{"speaker":"Jane","voice":"Puck"}]},"temperature":0.8},
              "response_format":{"type":"audio","mime_type":"audio/mulaw","sample_rate":8000},
              "future_option":{"enabled":true}, "stream":false
            }
            """);
        var options = native.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (topLevelConfig)
        {
            options["speech_config"] = native.GetProperty("generation_config").GetProperty("speech_config");
            options["generation_config"] = Json("""{"temperature":0.8}""");
        }
        var handler = new RecordingHandler(Audio + Completed);
        var provider = CreateProvider(handler);
        if (openAI)
            await Collect(provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest { Model = Model, Input = "script", Voice = "Charon", Instructions = "generic style", AdditionalProperties = options }));
        else
        {
            var request = Request();
            request.Instructions = "generic style";
            request.ProviderOptions = Options(JsonSerializer.SerializeToElement(options));
            request.Text = [new() { Text = "", Voice = "Kore" }, new() { Text = "", Voice = "Puck" }];
            var events = await Collect(provider.SpeechStreamingAsync(request));
            var warning = JsonSerializer.SerializeToElement(Assert.Single(Assert.IsType<SpeechStreamStartPart>(events[0]).Warnings));
            Assert.Equal("ignored", warning.GetProperty("type").GetString());
            Assert.Equal("instructions", warning.GetProperty("feature").GetString());
            var audio = Assert.Single(events.OfType<SpeechAudioPart>());
            Assert.Equal("audio/mulaw", audio.AudioFormat.Type);
            Assert.Equal(8000, audio.AudioFormat.Rate);
        }
        Assert.True(JsonElement.DeepEquals(native.GetProperty("input"), handler.Payload.GetProperty("input")));
        Assert.True(JsonElement.DeepEquals(native.GetProperty("generation_config"), handler.Payload.GetProperty("generation_config")));
        Assert.True(JsonElement.DeepEquals(native.GetProperty("response_format"), handler.Payload.GetProperty("response_format")));
        Assert.True(handler.Payload.GetProperty("future_option").GetProperty("enabled").GetBoolean());
        Assert.True(handler.Payload.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task OpenAI_unary_also_preserves_extension_data()
    {
        var handler = new RecordingHandler("""{"output_audio":{"data":"AQID","mime_type":"audio/l16"}}""", mediaType: "application/json");
        await CreateProvider(handler).OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = Model,
            Input = "script",
            AdditionalProperties = new() { ["speech_config"] = Json("""{"speakers":[{"speaker":"Joe","voice":"Kore"},{"speaker":"Jane","voice":"Puck"}]}""") }
        });
        Assert.Equal(2, handler.Payload.GetProperty("generation_config").GetProperty("speech_config").GetProperty("speakers").GetArrayLength());
    }

    [Theory]
    [InlineData("pcm", "audio/l16")]
    [InlineData("audio/wav", "audio/wav")]
    [InlineData("mp3", "audio/mp3")]
    [InlineData("opus", "audio/ogg_opus")]
    [InlineData("audio/alaw", "audio/alaw")]
    public async Task Generic_output_format_maps_to_native_response_format(string requested, string mime)
    {
        var handler = new RecordingHandler(Audio + Completed);
        var request = Request();
        request.OutputFormat = new() { Type = requested, Rate = 16000 };
        var events = await Collect(CreateProvider(handler).SpeechStreamingAsync(request));
        var format = handler.Payload.GetProperty("response_format");
        Assert.Equal(mime, format.GetProperty("mime_type").GetString());
        Assert.Equal(16000, format.GetProperty("sample_rate").GetInt32());
        var audio = Assert.Single(events.OfType<SpeechAudioPart>());
        Assert.Equal(mime, audio.AudioFormat.Type);
        Assert.Equal(16000, audio.AudioFormat.Rate);
        Assert.Null(Assert.Single(events.OfType<SpeechFinishPart>()).Usage);
    }

    [Fact]
    public async Task Part_options_override_request_defaults_and_native_format_wins_over_generic()
    {
        var handler = new RecordingHandler(Audio + Completed);
        var request = Request();
        request.OutputFormat = new() { Type = "pcm", Rate = 24000 };
        request.ProviderOptions = Options(Json("""{"generation_config":{"temperature":0.2},"response_format":{"type":"audio","mime_type":"audio/alaw","sample_rate":8000}}"""));
        request.Text = [new() { Text = "hello", ProviderOptions = Options(Json("""{"generation_config":{"temperature":0.8}}""")) }];
        var events = await Collect(CreateProvider(handler).SpeechStreamingAsync(request));
        Assert.Equal(0.8, handler.Payload.GetProperty("generation_config").GetProperty("temperature").GetDouble());
        Assert.Equal("audio/alaw", Assert.Single(events.OfType<SpeechAudioPart>()).AudioFormat.Type);
        Assert.Equal(2, Assert.IsType<SpeechStreamStartPart>(events[0]).Warnings.Count());
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("options")]
    public async Task Unrepresentable_part_changes_are_rejected_before_network_io(string change)
    {
        var handler = new RecordingHandler(Audio + Completed);
        var request = Request();
        request.Text = [new() { Text = "hel" }, new() { Text = "lo" }];
        if (change == "voice")
            request.Text[1].Voice = "Puck";
        else
            request.Text[1].ProviderOptions = Options(Json("""{"generation_config":{"temperature":0.5}}"""));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(CreateProvider(handler).SpeechStreamingAsync(request)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Identical_effective_part_configuration_is_allowed()
    {
        var handler = new RecordingHandler(Audio + Completed);
        var request = Request();
        request.Model += "/Puck";
        request.Text = [new() { Text = "hel", Voice = "Kore" }, new() { Text = "lo", Voice = "Charon" }];
        await Collect(CreateProvider(handler).SpeechStreamingAsync(request));
        Assert.Equal("hello", handler.Payload.GetProperty("input").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_reader_handles_multiline_frames_comments_arrays_and_nested_usage_without_repeating_snapshots(bool openAI)
    {
        var body = ": heartbeat\r\nid: ignored\r\nretry: 100\r\nevent: step.delta\r\ndata: {\r\ndata: \"event_type\":\"step.delta\",\"index\":2,\r\ndata: \"delta\":[{\"type\":\"audio\",\"data\":\"AQ==\"},{\"type\":\"audio\",\"data\":\"Ag==\"}]}\r\n\r\n"
            + Frame("step.stop", new { index = 2, metadata = new { total_usage = new { total_input_tokens = 4, total_tokens = 10 } } })
            + Frame("interaction.completed", new { interaction = new { status = "completed", usage = new { total_output_tokens = 6 }, steps = new[] { new { type = "audio", data = "AQI=" } } } });
        var events = await ReadBoth(CreateProvider(new RecordingHandler(body)), openAI);
        Assert.Equal(new[] { "AQ==", "Ag==" }, AudioValues(events));
        if (openAI)
        {
            var done = Assert.Single(events.OfType<AudioSpeechStreamDone>());
            Assert.Equal(4, done.Usage!.InputTokens);
            Assert.Equal(6, done.Usage.OutputTokens);
            Assert.Equal(10, done.Usage.TotalTokens);
        }
        else
        {
            var done = Assert.Single(events.OfType<SpeechFinishPart>());
            Assert.Equal(4, done.Usage!.InputTokens);
            Assert.Equal(6, done.Usage.OutputTokens);
        }
    }

    [Fact]
    public async Task Raw_events_and_metadata_only_audio_preserve_formats_and_remain_serializable_after_disposal()
    {
        var body = Frame("step.delta", new { index = 3, delta = new { type = "audio", mime_type = "audio/wav", sample_rate = 48000 } })
            + Frame("step.delta", new { index = 4, delta = new { type = "audio", mime_type = "audio/mulaw", sample_rate = 8000, data = "AQ==" } })
            + Frame("step.delta", new { index = 3, delta = new { type = "audio", data = "Ag==" } }) + Completed;
        var request = Request();
        request.IncludeRawChunks = true;
        var events = await Collect(CreateProvider(new RecordingHandler(body)).SpeechStreamingAsync(request));
        Assert.Equal(4, events.OfType<SpeechRawPart>().Count());
        Assert.Equal(2, events.OfType<SpeechAudioPart>().Count());
        var audio = events.OfType<SpeechAudioPart>().Last();
        Assert.Equal("audio/wav", audio.AudioFormat.Type);
        Assert.Equal(48000, audio.AudioFormat.Rate);
        foreach (var item in events)
            Assert.NotEmpty(JsonSerializer.Serialize<StreamingSpeechPart>(item));
    }

    public static IEnumerable<object[]> BrokenStreams()
    {
        var streams = new[]
        {
            "",
            Audio,
            Audio + "data: [DONE]\n\n",
            Audio + "event: error\ndata: quota exhausted\n\n",
            Audio + "data: {\"event_type\":\"error\",\"error\":{\"message\":\"quota\"}}\n\n",
            Audio + Frame("interaction.completed", new { interaction = new { status = "failed" } }),
            Audio + Frame("interaction.completed", new { interaction = new { status = "cancelled" } }),
            Audio + Frame("interaction.completed", new { interaction = new { status = "incomplete" } }),
            Audio + Frame("interaction.status_update", new { status = "failed" }),
            "data: {bad}\n\n",
            "data: []\n\n",
            "event: step.delta\ndata: {\"event_type\":\"interaction.completed\"}\n\n",
            Frame("step.delta", new { delta = new { type = "audio", data = "invalid!" } }) + Completed,
            Frame("step.delta", new { delta = new { type = "audio", data = 42 } }) + Completed,
            Frame("step.delta", new { delta = new { type = "audio", data = "AQ==", sample_rate = -1 } }) + Completed,
            Frame("step.delta", new { delta = new { type = "audio", uri = "https://example.com/audio" } }) + Completed,
            Completed,
            Frame("step.delta", new { delta = new { type = "audio", data = "" } }) + Completed,
            Audio + Frame("interaction.completed", new { usage = "invalid" })
        };
        foreach (var body in streams)
            foreach (var openAI in new[] { false, true })
                yield return [openAI, body];
    }

    [Theory]
    [MemberData(nameof(BrokenStreams))]
    public async Task Failures_never_emit_successful_finish(bool openAI, string body)
    {
        var events = new List<object>();
        var handler = new RecordingHandler(body);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in ReadBothStream(CreateProvider(handler), openAI))
                events.Add(item);
        });
        Assert.DoesNotContain(events, e => e is SpeechFinishPart or AudioSpeechStreamDone);
        Assert.True(handler.Body.Disposed);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.BadRequest, "application/json")]
    [InlineData(true, HttpStatusCode.BadRequest, "application/json")]
    [InlineData(false, HttpStatusCode.OK, "application/json")]
    [InlineData(true, HttpStatusCode.OK, "application/json")]
    public async Task Http_errors_and_non_sse_responses_fail_and_dispose(bool openAI, HttpStatusCode status, string mime)
    {
        var handler = new RecordingHandler("{\"error\":\"bad request\"}", status, mime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadBoth(CreateProvider(handler), openAI));
        Assert.True(handler.Body.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audio_is_delivered_before_upstream_completion_and_early_disposal_releases_body(bool openAI)
    {
        var body = new GatedStream(Audio, Completed);
        var handler = new RecordingHandler(body);
        await using (var iterator = ReadBothStream(CreateProvider(handler), openAI).GetAsyncEnumerator())
        {
            do
            {
                Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            } while (iterator.Current is not (SpeechAudioPart or AudioSpeechStreamDelta));
            Assert.False(body.TailRequested);
        }
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_while_waiting_for_more_audio_does_not_emit_finish(bool openAI)
    {
        var body = new GatedStream(Audio, Completed);
        var handler = new RecordingHandler(body);
        using var cancellation = new CancellationTokenSource();
        var events = new List<object>();
        var consuming = Task.Run(async () =>
        {
            await foreach (var item in ReadBothStream(CreateProvider(handler), openAI, cancellation.Token))
                events.Add(item);
        });
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consuming);
        Assert.DoesNotContain(events, e => e is SpeechFinishPart or AudioSpeechStreamDone);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_cancelled_requests_do_not_send(bool openAI)
    {
        var handler = new RecordingHandler(Audio + Completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadBoth(CreateProvider(handler), openAI, new CancellationToken(true)));
        Assert.Equal(0, handler.Calls);
    }

    private static StreamingSpeechRequest Request() => new() { Model = Model, Text = [new() { Text = "hello" }] };
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static Dictionary<string, JsonElement> Options(JsonElement native) => new() { ["google"] = native };
    private static string Frame(string type, object payload)
    {
        var obj = JsonSerializer.SerializeToNode(payload)!.AsObject();
        obj["event_type"] = type;
        return $"event: {type}\ndata: {obj.ToJsonString()}\n\n";
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private static Task<List<object>> ReadBoth(GoogleAIProvider provider, bool openAI, CancellationToken cancellationToken = default)
        => Collect(ReadBothStream(provider, openAI, cancellationToken));

    private static async IAsyncEnumerable<object> ReadBothStream(GoogleAIProvider provider, bool openAI,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (openAI)
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest { Model = Model, Input = "hello" }, cancellationToken))
                yield return item;
        }
        else
            await foreach (var item in provider.SpeechStreamingAsync(Request(), cancellationToken))
                yield return item;
    }

    private static IEnumerable<string> AudioValues(IEnumerable<object> events)
        => events.Select(e => e switch { SpeechAudioPart audio => audio.Audio, AudioSpeechStreamDelta audio => audio.Audio, _ => null }).OfType<string>();

    private static GoogleAIProvider CreateProvider(RecordingHandler handler)
        => new(new FixedKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), NullLogger<GoogleAIProvider>.Instance, new ClientFactory(new HttpClient(handler)));

    private sealed class FixedKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;
        public TrackingStream Body { get; }
        public JsonElement Payload { get; private set; }
        public string? Path { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = [];
        public int Calls { get; private set; }

        public RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/event-stream")
            : this(new TrackingStream(Encoding.UTF8.GetBytes(body)), status, mediaType) { }

        public RecordingHandler(TrackingStream body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/event-stream")
        {
            Body = body;
            response = new(status) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new(mediaType);
            response.Headers.Add("x-request-id", "request-123");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Payload = Json(await request.Content!.ReadAsStringAsync(cancellationToken));
            Path = request.RequestUri!.AbsolutePath;
            Headers = request.Headers.ToDictionary(p => p.Key, p => string.Join(",", p.Value), StringComparer.OrdinalIgnoreCase);
            return response;
        }
    }

    private class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class GatedStream(string prefix, string tail) : TrackingStream(Encoding.UTF8.GetBytes(prefix))
    {
        private readonly byte[] tailBytes = Encoding.UTF8.GetBytes(tail);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TailRequested { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0)
                return read;
            TailRequested = true;
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            tailBytes.AsMemory().CopyTo(buffer);
            return tailBytes.Length;
        }
    }
}
