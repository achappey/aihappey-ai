using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Deepgram;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.Deepgram;

public sealed class DeepgramTranscriptionRegressionTests
{
    private const string Result = """
        {"metadata":{"duration":3.5,"request_id":"original"},"results":{
          "channels":[
            {"detected_language":"en","alternatives":[{"transcript":"Hello.","words":[{"word":"hello","punctuated_word":"Hello.","start":1.125,"end":2,"speaker":0}]}]},
            {"detected_language":"nl","alternatives":[{"transcript":"Hoi.","words":[{"word":"hoi","punctuated_word":"Hoi.","start":0.25,"end":1,"speaker":1}]}]}],
          "utterances":[{"transcript":"Hello.","start":1.125,"end":2,"speaker":0,"channel":0},
                        {"transcript":"Hoi.","start":0.25,"end":1,"speaker":1,"channel":1}],
          "topics":{"preserved":true}}}
        """;

    [Fact]
    public async Task Rest_preserves_raw_results_normalizes_all_channels_and_sets_per_request_auth()
    {
        var keys = new Keys();
        var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("Token", request.Headers.Authorization?.Scheme);
            Assert.Equal(keys.Value, request.Headers.Authorization?.Parameter);
            Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content!.ReadAsByteArrayAsync());
            return Json(Result);
        }));
        var provider = new DeepgramProvider(keys, new Factory(client));
        var request = Rest();
        request.Audio = "data:audio/wav;base64,AQID";
        var result = await provider.TranscriptionRequest(request);
        keys.Value = "rotated";
        await provider.TranscriptionRequest(request);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal("Hello.\nHoi.", result.Text);
        Assert.Equal("multi", result.Language);
        Assert.Equal(3.5f, result.DurationInSeconds);
        Assert.Equal(new[] { "Hoi.", "Hello." }, result.Segments.Select(s => s.Text));
        Assert.True(result.ProviderMetadata!["deepgram"].GetProperty("results").GetProperty("topics").GetProperty("preserved").GetBoolean());
        Assert.Equal(Result, result.Response.Body);
    }

    [Fact]
    public async Task Rest_options_are_repeated_escaped_invariant_and_do_not_forward_adapter_option()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            var provider = Provider(request =>
            {
                var query = request.RequestUri!.Query;
                Assert.Contains("tag=a%26b&tag=two", query);
                Assert.Contains("extra=x%3Dy", query);
                Assert.Contains("utt_split=0.8", query);
                Assert.Contains("utterances=true", query);
                Assert.Contains("detect_language=en&detect_language=nl", query);
                Assert.Contains("summarize=v2", query);
                Assert.DoesNotContain("timestamp_granularities", query);
                return Json(Result);
            });
            await provider.TranscriptionRequest(Rest("""{"tag":["a&b","two"],"extra":"x=y","utt_split":0.8,"timestamp_granularities":["word","segment"],"detect_language":["en","nl"],"summarize":"v2","dictation":false,"keyterm":["Deepgram"]}"""));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("{\"callback\":\"https://example.com\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"prompt\":\"lost\"}")]
    [InlineData("{\"punctuate\":\"true\"}")]
    [InlineData("{\"tag\":[\"valid\",7]}")]
    [InlineData("{\"detect_language\":\"en\"}")]
    [InlineData("{\"summarize\":\"v1\"}")]
    [InlineData("{\"keywords\":\"boost:2\"}")]
    [InlineData("{\"keyterm\":\"boost:2\"}")]
    [InlineData("{\"language_hint\":\"en\"}")]
    [InlineData("{\"timestamp_granularities\":[\"bad\"]}")]
    [InlineData("{\"timestamp_granularities\":[\"segment\"],\"utterances\":false}")]
    [InlineData("{\"channels\":1.2}")]
    [InlineData("{\"tag\":null}")]
    public async Task Rest_rejects_invalid_or_unsupported_options_before_http(string options)
    {
        var provider = Provider(_ => throw new Exception("HTTP must not be invoked"));
        var exception = await Record.ExceptionAsync(() => provider.TranscriptionRequest(Rest(options)));
        Assert.True(exception is ArgumentException or NotSupportedException, exception?.ToString());
    }

    [Fact]
    public async Task Model_specific_options_and_language_multi_work()
    {
        var provider = Provider(r =>
        {
            Assert.Contains("language_hint=en&language_hint=nl", r.RequestUri!.Query);
            return Json(Result);
        });
        await provider.TranscriptionRequest(Rest("""{"language":"multi","language_hint":["en","nl"],"keyterm":"some term"}"""));
        var old = Rest("""{"keywords":["some:2","other"]}""");
        old.Model = "nova-2";
        await Provider(_ => Json(Result)).TranscriptionRequest(old);
        old.ProviderOptions = Options("""{"keyterm":"some"}""");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(old));
    }

    [Fact]
    public async Task Remote_audio_and_http_failures_are_not_silently_accepted()
    {
        var request = Rest();
        request.Audio = "https://example.com/audio.wav";
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(_ => Json(Result)).TranscriptionRequest(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("provider error") }).TranscriptionRequest(Rest()));
    }

    [Fact]
    public async Task Openai_verbose_populates_words_and_segments_together_and_duration_usage()
    {
        var request = OpenAI("verbose_json");
        request.TimestampGranularities = ["word", "segment"];
        var result = Assert.IsType<OpenAITranscriptionVerboseResponse>(await Provider(r =>
        {
            Assert.Contains("utterances=true", r.RequestUri!.Query);
            return Json(Result);
        }).OpenAITranscriptionRequestAsync(request));
        Assert.Equal(2, result.Words!.Length);
        Assert.Equal(2, result.Segments!.Length);
        Assert.Equal("Hoi.", result.Words[0].Word);
        Assert.Equal(1.125, result.Segments[1].Start);
        Assert.Equal(3.5, Assert.IsType<OpenAITranscriptionDurationUsage>(result.Usage).Seconds);
    }

    [Theory]
    [InlineData("srt", "1\n00:00:00,250 --> 00:00:01,000\nHoi.")]
    [InlineData("vtt", "WEBVTT\n\n1\n00:00:00.250 --> 00:00:01.000\nHoi.")]
    public async Task Openai_subtitles_use_real_timestamps(string format, string expected)
    {
        var response = await Provider(_ => Json(Result)).OpenAITranscriptionRequestAsync(OpenAI(format));
        Assert.StartsWith(expected, response.Text);
        Assert.Contains("00:00:01" + (format == "srt" ? ",125" : ".125"), response.Text);
    }

    [Fact]
    public async Task Openai_diarized_uses_channel_scoped_speakers()
    {
        var result = Assert.IsType<OpenAITranscriptionDiarizedResponse>(await Provider(r =>
        {
            Assert.Contains("diarize_model=latest", r.RequestUri!.Query);
            return Json(Result);
        }).OpenAITranscriptionRequestAsync(OpenAI("diarized_json")));
        Assert.Equal(new[] { "channel_1_speaker_1", "channel_0_speaker_0" }, result.Segments.Select(s => s.Speaker));
    }

    [Fact]
    public async Task Openai_rejects_prompt_temperature_and_extra_options()
    {
        var provider = Provider(_ => throw new Exception("Unexpected HTTP"));
        var request = OpenAI();
        request.Prompt = "do not invent keyword mapping";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAITranscriptionRequestAsync(request));
        request.Prompt = null;
        request.Temperature = 0;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAITranscriptionRequestAsync(request));
        request.Temperature = null;
        request.AdditionalProperties = Options("{}");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAITranscriptionRequestAsync(request));
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    [InlineData("diarized_json")]
    public void Expanded_formats_require_resolved_deepgram_and_keep_other_validation(string format)
    {
        var request = OpenAI(format);
        Assert.Throws<NotSupportedException>(() => request.ValidateOpenAITranscriptionRequest());
        Assert.Throws<NotSupportedException>(() => request.ValidateOpenAITranscriptionRequest("other"));
        request.ValidateOpenAITranscriptionRequest("deepgram");
        request.Include = ["logprobs"];
        Assert.Throws<NotSupportedException>(() => request.ValidateOpenAITranscriptionRequest("deepgram"));
    }

    [Fact]
    public async Task Websocket_receives_while_uploading_fragments_revisions_and_deduplicates_finals()
    {
        var socket = new ScriptSocket([
            Event("hel", false), Event("hello", false), Event("Hello.", true), Event("Hello.", true),
            """{"type":"SpeechStarted","timestamp":1}""", """{"type":"UtteranceEnd","last_word_end":2}""",
            Event("Hoi.", true, 1, 2), """{"type":"Metadata","duration":3.5}"""]);
        var provider = SocketProvider(socket, uri =>
        {
            Assert.Equal("wss", uri.Scheme);
            Assert.Equal("api.deepgram.com", uri.Host);
            Assert.DoesNotContain("encoding", uri.Query);
        });
        var request = Stream();
        request.IncludeRawChunks = true;
        var parts = await Collect(provider.TranscriptionStreamingAsync(request));
        Assert.Equal(new[] { "hel", "hello" }, parts.OfType<TranscriptionPartialPart>().Select(p => p.Text));
        Assert.Equal(2, parts.OfType<TranscriptionFinalPart>().Count());
        Assert.Equal(0, parts.OfType<TranscriptionFinalPart>().First().ChannelIndex);
        var finish = Assert.Single(parts.OfType<TranscriptionFinishPart>());
        Assert.Equal("Hello. Hoi.", finish.Text);
        Assert.Equal(3.5, finish.DurationInSeconds);
        Assert.Equal(8, parts.OfType<TranscriptionRawPart>().Count());
        Assert.Equal(3, parts.OfType<TranscriptionResponseMetadataPart>().Count());
        Assert.True(socket.ReceivedBeforeUpload);
        Assert.True(socket.CloseStreamSent);
        Assert.Equal(100000, socket.BinarySizes.Sum());
        Assert.All(socket.BinarySizes, size => Assert.InRange(size, 1, 32768));
        Assert.True(socket.AcknowledgedClose);
    }

    [Fact]
    public async Task Openai_stream_is_append_only_committed_text_and_one_done()
    {
        var socket = new ScriptSocket([Event("provisional", false), Event("Hello.", true), Event("Hello.", true),
            Event("World.", true, 0, 2), """{"type":"Metadata","duration":3}"""]);
        var parts = await Collect(SocketProvider(socket).OpenAITranscriptionStreamingAsync(OpenAI()));
        Assert.Equal("Hello. World.", string.Concat(parts.OfType<OpenAITranscriptionTextDelta>().Select(p => p.Delta)));
        Assert.Equal("Hello. World.", Assert.Single(parts.OfType<OpenAITranscriptionTextDone>()).Text);
    }

    [Theory]
    [InlineData("{\"sentiment\":true}")]
    [InlineData("{\"utterances\":true}")]
    [InlineData("{\"diarize_model\":\"v2\"}")]
    [InlineData("{\"callback\":\"https://example.com\"}")]
    [InlineData("{\"endpointing\":\"false\"}")]
    public async Task Websocket_rejects_batch_only_and_invalid_options(string options)
    {
        var request = Stream();
        request.ProviderOptions = Options(options);
        var provider = Provider(_ => throw new Exception("Unexpected HTTP"));
        provider.TranscriptionSocketFactory = (_, _, _) => throw new Exception("Unexpected connection");
        var error = await Record.ExceptionAsync(() => Collect(provider.TranscriptionStreamingAsync(request)));
        Assert.True(error is ArgumentException or NotSupportedException, error?.ToString());
    }

    [Fact]
    public async Task Pcm_requires_channels_and_consistent_rate_container_does_not_force_encoding()
    {
        var request = Stream();
        request.InputAudioFormat = new() { Type = "pcm16", Rate = 16000 };
        var provider = SocketProvider(new ScriptSocket(["""{"type":"Metadata","duration":1}"""]), uri =>
        {
            Assert.Contains("encoding=linear16", uri.Query);
            Assert.Contains("sample_rate=16000", uri.Query);
            Assert.Contains("channels=2", uri.Query);
        });
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(provider.TranscriptionStreamingAsync(request)));
        request.ProviderOptions = Options("""{"channels":2,"sample_rate":8000}""");
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(provider.TranscriptionStreamingAsync(request)));
        request.ProviderOptions = Options("""{"channels":2}""");
        await Collect(provider.TranscriptionStreamingAsync(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fatal_errors_and_unclean_close_do_not_emit_finish(bool fatalEvent)
    {
        var socket = new ScriptSocket(fatalEvent
            ? [Event("Committed", true), """{"type":"Error","description":"failure"}"""]
            : [Event("Committed", true), """{"type":"Metadata","duration":1}"""], cleanClose: fatalEvent);
        var parts = new List<StreamingTranscriptionPart>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var part in SocketProvider(socket).TranscriptionStreamingAsync(Stream())) parts.Add(part);
        });
        Assert.Empty(parts.OfType<TranscriptionFinishPart>());
        if (fatalEvent) Assert.Single(parts.OfType<TranscriptionErrorPart>());
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task Nonfatal_error_preserves_event_and_allows_completion()
    {
        var socket = new ScriptSocket(["""{"type":"Error","fatal":false,"description":"recoverable"}""", Event("Hello", true), """{"type":"Metadata","duration":1}"""]);
        var parts = await Collect(SocketProvider(socket).TranscriptionStreamingAsync(Stream()));
        Assert.Single(parts.OfType<TranscriptionErrorPart>());
        Assert.Single(parts.OfType<TranscriptionFinishPart>());
    }

    [Fact]
    public async Task Cancellation_and_early_disposal_abort_and_observe_upload()
    {
        var socket = new ScriptSocket([], stall: true);
        using var cancellation = new CancellationTokenSource();
        var provider = SocketProvider(socket);
        await using (var iterator = provider.TranscriptionStreamingAsync(Stream(), cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            var pending = iterator.MoveNextAsync().AsTask();
            await socket.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.True(socket.Aborted);
        var early = new ScriptSocket([], stall: true);
        await using (var iterator = SocketProvider(early).TranscriptionStreamingAsync(Stream()).GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        Assert.True(early.Aborted);
        Assert.False(early.CloseStreamSent);
    }

    [Fact]
    public async Task Missing_terminal_metadata_never_finishes()
    {
        var socket = new ScriptSocket([Event("hello", true)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(SocketProvider(socket).TranscriptionStreamingAsync(Stream())));
    }

    private static string Event(string text, bool final, int channel = 0, int start = 0)
        => JsonSerializer.Serialize(new { type = "Results", channel_index = new[] { channel, 2 }, start, duration = 1,
            is_final = final, speech_final = final, channel = new { alternatives = new[] { new { transcript = text } } } });
    private static Dictionary<string, JsonElement> Options(string json) => new() { ["deepgram"] = JsonDocument.Parse(json).RootElement.Clone() };
    private static TranscriptionRequest Rest(string options = "{}") => new()
    { Model = "nova-3", Audio = "AQID", MediaType = "audio/wav", ProviderOptions = Options(options) };
    private static StreamingTranscriptionRequest Stream() => new()
    { Model = "nova-3", Audio = Convert.ToBase64String(new byte[100000]), InputAudioFormat = new() { Type = "audio/wav" } };
    private static OpenAITranscriptionRequest OpenAI(string format = "json") => new()
    {
        Model = "nova-3", ResponseFormat = format,
        File = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "audio.wav")
        { Headers = new HeaderDictionary(), ContentType = "audio/wav" }
    };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static DeepgramProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new Keys(), new Factory(new HttpClient(new Handler(r => Task.FromResult(responder(r))))));
    private static DeepgramProvider SocketProvider(ScriptSocket socket, Action<Uri>? inspect = null)
    {
        var provider = Provider(_ => throw new Exception("Streaming must not use REST"));
        provider.TranscriptionSocketFactory = (uri, key, _) =>
        {
            Assert.Equal("test-key", key);
            inspect?.Invoke(uri);
            return Task.FromResult<WebSocket>(socket);
        };
        return provider;
    }
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
    private sealed class Keys : IApiKeyResolver
    {
        public string Value { get; set; } = "test-key";
        public string? Resolve(string provider) => Value;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }

    private sealed class ScriptSocket(IEnumerable<string> script, bool cleanClose = true, bool stall = false) : WebSocket
    {
        private readonly Queue<byte[]> _messages = new(script.Select(Encoding.UTF8.GetBytes));
        private readonly TaskCompletionSource _uploadComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _offset;
        private int _sending;
        public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int> BinarySizes { get; } = [];
        public bool ReceivedBeforeUpload { get; private set; }
        public bool CloseStreamSent { get; private set; }
        public bool Aborted { get; private set; }
        public bool AcknowledgedClose { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.CloseReceived ?
            cleanClose ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.InternalServerError : null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() { Aborted = true; _state = WebSocketState.Aborted; }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { AcknowledgedClose = true; return Task.CompletedTask; }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Assert.Equal(1, Interlocked.Increment(ref _sending));
            try
            {
                await ReceiveStarted.Task.WaitAsync(cancellationToken); // A sequential upload-then-receive implementation deadlocks.
                if (messageType == WebSocketMessageType.Binary) BinarySizes.Add(buffer.Count);
                else
                {
                    Assert.Equal("{\"type\":\"CloseStream\"}", Encoding.UTF8.GetString(buffer));
                    CloseStreamSent = true;
                    _uploadComplete.TrySetResult();
                }
            }
            finally { Interlocked.Decrement(ref _sending); }
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            ReceivedBeforeUpload |= !CloseStreamSent;
            ReceiveStarted.TrySetResult();
            if (stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (_messages.Count == 0)
            {
                await _uploadComplete.Task.WaitAsync(cancellationToken);
                _state = WebSocketState.CloseReceived;
                return new(0, WebSocketMessageType.Close, true, CloseStatus, null);
            }
            var message = _messages.Peek();
            var count = Math.Min(7, message.Length - _offset); // Fragment UTF-8 JSON at arbitrary boundaries.
            message.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            var end = _offset == message.Length;
            if (end) { _messages.Dequeue(); _offset = 0; }
            return new(count, WebSocketMessageType.Text, end);
        }
    }
}
