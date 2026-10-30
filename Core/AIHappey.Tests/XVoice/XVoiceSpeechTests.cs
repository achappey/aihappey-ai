using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.XVoice;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.XVoice;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.XVoice;

public sealed class XVoiceSpeechTests
{
    [Theory]
    [InlineData("wav", "audio/wav")]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("pcm", "audio/pcm")]
    public async Task Speech_preserves_documented_options_and_exact_input(string format, string mime)
    {
        var provider = Create(async (request, _) =>
        {
            Assert.Equal("/v1/audio/speech", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("key", request.Headers.Authorization?.Parameter);
            var payload = await Payload(request);
            Assert.Equal("cadence-tts", payload.GetProperty("model").GetString());
            Assert.Equal("clara", payload.GetProperty("voice").GetString());
            Assert.Equal(" Hola! ", payload.GetProperty("input").GetString());
            Assert.Equal("es", payload.GetProperty("language").GetString());
            Assert.Equal(format, payload.GetProperty("response_format").GetString());
            Assert.Equal(1, payload.GetProperty("speed").GetDouble());
            Assert.Equal("calm", payload.GetProperty("instructions").GetString());
            return Audio(mime);
        });
        var result = await provider.SpeechRequest(new()
        {
            Model = "xvoice/cadence-tts", Text = " Hola! ", Voice = "clara",
            Language = "es", OutputFormat = format, Speed = 1, Instructions = "calm"
        });
        Assert.Equal("AQID", result.Audio.Base64);
        Assert.Equal(mime, result.Audio.MimeType);
        Assert.Equal(format, result.Audio.Format);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Defaults_and_voice_shortcuts_do_not_restrict_future_models_or_voices()
    {
        var provider = Create(async (request, _) =>
        {
            var payload = await Payload(request);
            Assert.Equal("future-tts", payload.GetProperty("model").GetString());
            Assert.Equal("future-voice", payload.GetProperty("voice").GetString());
            Assert.Equal("wav", payload.GetProperty("response_format").GetString());
            return Audio();
        });
        await provider.OpenAISpeechRequestAsync(new() { Model = "xvoice/future-tts/future-voice", Input = "Hello" });
    }

    [Theory]
    [InlineData("clara")]
    [InlineData("maya")]
    [InlineData("ethan")]
    [InlineData("noah")]
    [InlineData("oliver")]
    public async Task All_documented_voices_and_nullable_typed_options_are_usable(string voice)
    {
        var provider = Create(async (request, _) =>
        {
            var body = await Payload(request);
            Assert.Equal(voice, body.GetProperty("voice").GetString());
            Assert.Equal("wav", body.GetProperty("response_format").GetString());
            Assert.Equal("es", body.GetProperty("language").GetString());
            return Audio();
        });
        var request = SpeechRequest();
        request.Voice = null;
        request.ProviderOptions = Native(new XVoiceSpeechProviderMetadata { Voice = voice, Language = "es", Speed = 1 });
        await provider.SpeechRequest(request);
    }

    [Fact]
    public async Task SSE_event_names_are_accepted_when_type_is_not_repeated_in_data()
    {
        var provider = Create((_, _) => Task.FromResult(Sse(
            "event: speech.audio.delta\ndata: {\"audio\":\"AQID\"}\n\n" +
            "event: speech.audio.done\ndata: {\"usage\":{\"characters\":5}}\n\n")));
        var events = await Drain(provider.SpeechStreamingAsync(StreamRequest()));
        Assert.Equal("AQID", Assert.Single(events.OfType<SpeechAudioPart>()).Audio);
        Assert.Single(events.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task Empty_or_non_audio_binary_responses_fail_explicitly()
    {
        var empty = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.SpeechRequest(SpeechRequest()));
        var json = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":{"code":"model_error"}}""", Encoding.UTF8, "application/json")
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => json.SpeechRequest(SpeechRequest()));
    }

    [Fact]
    public async Task OpenAI_extensions_and_scoped_options_are_preserved_with_standard_fields_winning()
    {
        var provider = Create(async (request, _) =>
        {
            var body = await Payload(request);
            Assert.Equal("es", body.GetProperty("language").GetString());
            Assert.Equal("clara", body.GetProperty("voice").GetString());
            Assert.Equal("hello", body.GetProperty("input").GetString());
            Assert.Equal("future", body.GetProperty("native_option").GetString());
            return Audio();
        });
        await provider.OpenAISpeechRequestAsync(new()
        {
            Model = "cadence-tts", Input = "hello", Voice = "clara",
            AdditionalProperties = new()
            {
                ["language"] = JsonSerializer.SerializeToElement("es"),
                ["input"] = JsonSerializer.SerializeToElement("wrong"),
                ["providerOptions"] = JsonSerializer.SerializeToElement(new { xvoice = new { voice = "maya", native_option = "future" } })
            }
        });
    }

    [Fact]
    public async Task Native_SSE_yields_first_audio_before_provider_finishes()
    {
        var stream = new GatedStream();
        var provider = Create(async (request, _) =>
        {
            var body = await Payload(request);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal("sse", body.GetProperty("stream_format").GetString());
            return new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } }
            };
        });
        await using var events = provider.SpeechStreamingAsync(new()
        {
            Model = "cadence-tts", Voice = "clara", Text = [new() { Text = "Hel" }, new() { Text = "lo" }],
            IncludeRawChunks = true
        }).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<SpeechStreamStartPart>(events.Current);
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<SpeechResponseMetadataPart>(events.Current);
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<SpeechRawPart>(events.Current);
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var audio = Assert.IsType<SpeechAudioPart>(events.Current);
        Assert.Equal("AQID", audio.Audio);
        Assert.Equal("audio/wav", audio.AudioFormat.Type);
        Assert.False(stream.Released);
        stream.Release();
        var rest = new List<StreamingSpeechPart>();
        while (await events.MoveNextAsync()) rest.Add(events.Current);
        var finish = Assert.Single(rest.OfType<SpeechFinishPart>());
        Assert.Equal(5, finish.Usage!.InputCharacters);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task OpenAI_stream_and_buffered_SSE_share_native_audio_events()
    {
        var provider = Create((_, _) => Task.FromResult(Sse(Delta + Done)));
        var events = await Drain(provider.OpenAISpeechStreamingAsync(new()
        {
            Model = "cadence-tts", Voice = "ethan", Input = "hello", StreamFormat = "sse", ResponseFormat = "pcm"
        }));
        Assert.Equal("AQID", Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[1]);
        var result = await provider.OpenAISpeechRequestAsync(new()
        {
            Model = "cadence-tts", Voice = "ethan", Input = "hello", StreamFormat = "sse", ResponseFormat = "pcm"
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio);
        Assert.Equal("audio/pcm", result.MimeType);
    }

    [Fact]
    public async Task Binary_stream_format_forwards_chunks_without_SSE_decoding()
    {
        var provider = Create(async (request, _) =>
        {
            Assert.Equal("audio", (await Payload(request)).GetProperty("stream_format").GetString());
            return Audio("audio/pcm");
        });
        var events = await Drain(provider.OpenAISpeechStreamingAsync(new()
        {
            Model = "cadence-tts", Voice = "noah", Input = "hello", StreamFormat = "audio", ResponseFormat = "pcm"
        }));
        Assert.Equal("AQID", Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[^1]);
    }

    [Theory]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"code\":\"stream_interrupted\"}}\n\n")]
    [InlineData("event: error\ndata: {\"code\":\"stream_interrupted\"}\n\n")]
    [InlineData("data: {\"type\":\"speech.audio.delta\",\"audio\":\"AQID\"}\n\n")]
    [InlineData("data: {\"type\":\"speech.audio.delta\",\"audio\":\"not base64\"}\n\n")]
    [InlineData("data: {\"type\":\"speech.audio.done\"}\n\n")]
    public async Task Error_truncated_invalid_or_empty_stream_never_yields_finish(string body)
    {
        var provider = Create((_, _) => Task.FromResult(Sse(body)));
        var events = new List<StreamingSpeechPart>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in provider.SpeechStreamingAsync(StreamRequest())) events.Add(item);
        });
        Assert.Empty(events.OfType<SpeechFinishPart>());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Http_errors_preserve_status_body_request_id_and_retry_after(int status)
    {
        var provider = Create((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"code":"model_overloaded","message":"retry later"}}""")
            };
            response.Headers.Add("x-request-id", "req-123");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(SpeechRequest()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("model_overloaded", error.Message);
        Assert.Contains("req-123", error.Message);
        Assert.Contains("retry-after=3", error.Message);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_more_SSE_audio_disposes_response()
    {
        using var cts = new CancellationTokenSource();
        var stream = new GatedStream();
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } }
        }));
        await using var events = provider.OpenAISpeechStreamingAsync(new()
        {
            Model = "cadence-tts", Input = "hello", Voice = "oliver"
        }, cts.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<AudioSpeechStreamDelta>(events.Current);
        var pending = events.MoveNextAsync().AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Parts_merge_options_and_preserve_midword_boundaries_without_mutating_request()
    {
        var provider = Create(async (request, _) =>
        {
            var body = await Payload(request);
            Assert.Equal("hello", body.GetProperty("input").GetString());
            Assert.Equal("maya", body.GetProperty("voice").GetString());
            Assert.Equal("es", body.GetProperty("language").GetString());
            return Sse(Delta + Done);
        });
        var request = StreamRequest();
        request.ProviderOptions = Native(new { voice = "clara", language = "en" });
        request.Text = [
            new() { Text = "hel", Voice = "maya", ProviderOptions = Native(new { language = "es" }) },
            new() { Text = "lo", Voice = "maya", ProviderOptions = Native(new { language = "es" }) }
        ];
        var original = JsonSerializer.Serialize(request);
        await Drain(provider.SpeechStreamingAsync(request));
        Assert.Equal(original, JsonSerializer.Serialize(request));
        request.Text[1].Voice = "clara";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(request)));
    }

    [Fact]
    public async Task Validation_covers_limits_missing_fields_and_stream_configuration()
    {
        var provider = Create((_, _) => throw new InvalidOperationException("No HTTP expected."));
        foreach (var text in new[] { "", " ", new string('a', 8001) })
        {
            var request = SpeechRequest();
            request.Text = text;
            await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(request));
        }
        var noVoice = SpeechRequest();
        noVoice.Voice = null;
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(noVoice));
        var longInstructions = SpeechRequest();
        longInstructions.Instructions = new string('a', 1001);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(longInstructions));
        var stream = StreamRequest();
        stream.Speed = 1.1;
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(provider.SpeechStreamingAsync(stream)));
        stream.Speed = 1;
        stream.OutputFormat = new() { Type = "pcm", Rate = 16000 };
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(stream)));
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(provider.OpenAISpeechStreamingAsync(new()
        {
            Model = "cadence-tts", Input = "hello", Voice = "clara", StreamFormat = "invalid"
        })));
    }

    [Fact]
    public async Task Input_limit_counts_unicode_codepoints_and_dynamic_formats_are_not_hardcoded()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F30D", 8000));
        var provider = Create(async (request, _) =>
        {
            var body = await Payload(request);
            Assert.Equal(text, body.GetProperty("input").GetString());
            Assert.Equal("future-format", body.GetProperty("response_format").GetString());
            Assert.Equal(1.5, body.GetProperty("speed").GetDouble());
            return Audio("audio/future-format");
        });
        var request = SpeechRequest();
        request.Model = "future-model";
        request.Text = text;
        request.OutputFormat = "future-format";
        request.Speed = 1.5f;
        await provider.SpeechRequest(request);
    }

    private const string Delta = "event: speech.audio.delta\ndata: {\"type\":\"speech.audio.delta\",\"audio\":\"AQID\",\"response_format\":\"pcm\"}\n\n";
    private const string Done = "event: speech.audio.done\ndata: {\"type\":\"speech.audio.done\",\"usage\":{\"characters\":5}}\n\n";
    private static SpeechRequest SpeechRequest() => new() { Model = "cadence-tts", Text = "hello", Voice = "clara" };
    private static StreamingSpeechRequest StreamRequest() => new()
    {
        Model = "cadence-tts", Voice = "clara", Text = [new() { Text = "hello" }]
    };
    private static Dictionary<string, JsonElement> Native(object value) => new() { ["xvoice"] = JsonSerializer.SerializeToElement(value) };
    private static async Task<JsonElement> Payload(HttpRequestMessage request)
        => JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
    private static HttpResponseMessage Audio(string mime = "audio/wav") => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new(mime) } }
    };
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };
    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private static XVoiceProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Resolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Factory(new(new Handler(send))));
    private sealed class Resolver : IApiKeyResolver { public string? Resolve(string provider) => "key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class GatedStream : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stage;
        public bool Released => _release.Task.IsCompleted;
        public bool Disposed { get; private set; }
        public void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_stage == 0)
            {
                _stage++;
                var bytes = Encoding.UTF8.GetBytes(Delta.Replace("pcm", "wav"));
                bytes.CopyTo(buffer);
                return bytes.Length;
            }
            if (_stage == 1)
            {
                await _release.Task.WaitAsync(cancellationToken);
                _stage++;
                var bytes = Encoding.UTF8.GetBytes(Done);
                bytes.CopyTo(buffer);
                return bytes.Length;
            }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
