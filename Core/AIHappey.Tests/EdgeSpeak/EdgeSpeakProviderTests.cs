using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.EdgeSpeak;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.EdgeSpeak;

public sealed partial class EdgeSpeakProviderTests
{
    private const string Model = "openbmb/VoxCPM2";
    private const string Delta = "event: speech.audio.delta\ndata: {\"type\":\"speech.audio.delta\",\"audio\":\"UklGRg==\"}\n\n";
    private const string Done = "event: speech.audio.done\ndata: {\"type\":\"speech.audio.done\"}\n\n";

    [Fact]
    public async Task Speech_preserves_all_native_controls_and_native_rate_default()
    {
        var native = new
        {
            seed = 42, guidance_scale = 2.5, inference_steps = 20, temperature = 0.7,
            top_p = 0.8, top_k = 50, repetition_penalty = 1.1, retry_badcase = true,
            clone_recipe = "controllable", user_marks = new[] { new { word = "word", phoneme = "w" } },
            phonetic_spans = new[] { new { start = 0, end = 1, phoneme = "h" } },
            control_policy = new { mode = "strict" }
        };
        var provider = Create(async (message, _) =>
        {
            AssertAuth(message);
            Assert.Equal("/v1/audio/speech", message.RequestUri!.AbsolutePath);
            Assert.False(message.Headers.Contains("buffered-speech"));
            var body = await Payload(message);
            Assert.Equal(Model, body.GetProperty("model").GetString());
            Assert.Equal("builtin:bright-girl", body.GetProperty("voice").GetString());
            Assert.Equal("wav", body.GetProperty("response_format").GetString());
            Assert.Equal("audio", body.GetProperty("stream_format").GetString());
            Assert.Equal("en-US", body.GetProperty("language").GetString());
            Assert.Equal("Calm", body.GetProperty("instructions").GetString());
            Assert.Equal(1.25, body.GetProperty("speed").GetDouble());
            Assert.False(body.TryGetProperty("sample_rate", out var sampleRate));
            foreach (var field in JsonSerializer.SerializeToElement(native).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(field.Value, body.GetProperty(field.Name)));
            return Binary();
        });
        var response = await provider.SpeechRequest(new()
        {
            Model = "edgespeak/" + Model + "/builtin:bright-girl", Text = "Hello",
            Language = "en-US", Instructions = "Calm", Speed = 1.25f, ProviderOptions = Options(native)
        });
        Assert.Equal("UklGRg==", response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("scoped")]
    [InlineData("wrapper")]
    public async Task OpenAI_speech_supports_native_extensions_and_segments(string shape)
    {
        var native = JsonSerializer.SerializeToElement(new
        {
            language = "zh-TW", sample_rate = 24000,
            segments = new object[]
            {
                new { input = "One", instructions = (string?)null },
                new { input = "Two", voice = "user:voice", disable_style = true, speed = 0.8, language = "auto" }
            }
        });
        var extensions = shape switch
        {
            "scoped" => new Dictionary<string, JsonElement> { ["edgespeak"] = native },
            "wrapper" => new() { ["providerOptions"] = JsonSerializer.SerializeToElement(new { edgespeak = native }) },
            _ => native.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
        };
        var provider = Create(async (message, _) =>
        {
            var body = await Payload(message);
            Assert.False(body.TryGetProperty("input", out var input));
            Assert.Equal(24000, body.GetProperty("sample_rate").GetInt32());
            Assert.Equal("zh-TW", body.GetProperty("language").GetString());
            Assert.True(JsonElement.DeepEquals(native.GetProperty("segments"), body.GetProperty("segments")));
            return Binary();
        });
        var response = await provider.OpenAISpeechRequestAsync(new()
        {
            Model = Model, Voice = "builtin:auto", AdditionalProperties = extensions
        });
        Assert.Equal("RIFF", Encoding.ASCII.GetString(response.Audio));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("melody")]
    [InlineData("off")]
    public async Task Music_and_result_packages_opt_in_to_buffered_speech(string planningMode)
    {
        var provider = Create(async (message, _) =>
        {
            Assert.Equal("1", Assert.Single(message.Headers.GetValues("buffered-speech")));
            var body = await Payload(message);
            Assert.Equal(planningMode, body.GetProperty("music").GetProperty("planning_mode").GetString());
            Assert.Equal("zip", body.GetProperty("output_container").GetString());
            return Binary("application/zip");
        });
        var result = await provider.SpeechRequest(new()
        {
            Model = "m-a-p/YuE2-3B", Voice = "builtin:auto", Text = "Lyrics",
            ProviderOptions = Options(new { music = new { planning_mode = planningMode }, output_container = "zip" })
        });
        Assert.Equal("application/zip", result.Audio.MimeType);
        Assert.Equal("zip", result.Audio.Format);
    }

    [Fact]
    public async Task Music_ABC_and_audio_container_are_forwarded_in_buffered_mode()
    {
        const string abc = "X:1\nM:4/4\nK:C\nCDEF|";
        var provider = Create(async (message, _) =>
        {
            Assert.Equal("1", Assert.Single(message.Headers.GetValues("buffered-speech")));
            var body = await Payload(message);
            Assert.Equal(abc, body.GetProperty("music").GetProperty("abc").GetString());
            Assert.Equal("audio", body.GetProperty("output_container").GetString());
            return Binary();
        });
        var result = await provider.SpeechRequest(new()
        {
            Model = "m-a-p/YuE2-3B", Voice = "builtin:auto", Text = "Lyrics",
            ProviderOptions = Options(new { music = new { planning_mode = "full", abc }, output_container = "audio" })
        });
        Assert.Equal("audio/wav", result.Audio.MimeType);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(413)]
    [InlineData(422)]
    public async Task Native_HTTP_errors_are_preserved_by_buffered_speech(int status)
    {
        var provider = Create((_, _) => Task.FromResult(Json(
            """{"error":{"code":"language_unsupported","param":"language","message":"Choose another model"}}""",
            (HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(new()
        {
            Model = Model, Text = "Hello", Voice = "builtin:auto"
        }));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("language_unsupported", error.Message);
        Assert.Contains("\"param\":\"language\"", error.Message);
    }

    [Fact]
    public async Task Native_SSE_forwards_audio_before_server_finishes_on_both_stream_contracts()
    {
        foreach (var openAI in new[] { false, true })
        {
            var stream = new PushStream();
            stream.Push(": heartbeat\r\n\r\nid: 1\r\nevent: speech.audio.delta\r\ndata: {\"audio\":\r\ndata: \"UklGRg==\"}\r\n\r\n");
            var provider = Create(async (message, _) =>
            {
                AssertAuth(message);
                Assert.Equal("text/event-stream", message.Headers.Accept.ToString());
                Assert.Equal("sse", (await Payload(message)).GetProperty("stream_format").GetString());
                Assert.False(message.Headers.Contains("buffered-speech"));
                return Sse(stream);
            });
            if (openAI)
            {
                await using var iterator = provider.OpenAISpeechStreamingAsync(new()
                {
                    Model = Model, Voice = "builtin:auto", Input = "Hello", StreamFormat = "sse"
                }).GetAsyncEnumerator();
                Assert.True(await Next(iterator));
                Assert.Equal("UklGRg==", Assert.IsType<AudioSpeechStreamDelta>(iterator.Current).Audio);
                Assert.False(stream.Disposed);
                stream.Push(Delta + Done);
                Assert.True(await Next(iterator));
                Assert.IsType<AudioSpeechStreamDelta>(iterator.Current);
                Assert.True(await Next(iterator));
                Assert.IsType<AudioSpeechStreamDone>(iterator.Current);
                Assert.False(await Next(iterator));
            }
            else
            {
                var request = Streaming();
                request.IncludeRawChunks = true;
                await using var iterator = provider.SpeechStreamingAsync(request).GetAsyncEnumerator();
                var observed = new List<StreamingSpeechPart>();
                while (await Next(iterator))
                {
                    observed.Add(iterator.Current);
                    if (iterator.Current is SpeechAudioPart)
                        break;
                }
                Assert.Equal("UklGRg==", Assert.Single(observed.OfType<SpeechAudioPart>()).Audio);
                Assert.DoesNotContain(observed, x => x is SpeechFinishPart);
                Assert.False(stream.Disposed);
                stream.Push(Done);
                while (await Next(iterator))
                    observed.Add(iterator.Current);
                Assert.Single(observed.OfType<SpeechFinishPart>());
                Assert.Equal(2, observed.OfType<SpeechRawPart>().Count());
            }
            Assert.True(stream.Disposed);
        }
    }

    [Theory]
    [InlineData("event: error\ndata: {\"error\":{\"code\":\"service_busy\",\"message\":\"Busy\"}}\n\n")]
    [InlineData("data: {\"type\":\"error\",\"message\":\"Failed\"}\n\n")]
    [InlineData("event: speech.audio.delta\ndata: {\"audio\":\"!\"}\n\n")]
    [InlineData("event: speech.audio.delta\ndata: {}\n\n")]
    [InlineData("")]
    [InlineData("event: speech.audio.done\ndata: {}")]
    public async Task Speech_stream_failures_never_emit_success(string tail)
    {
        var provider = Create((_, _) => Task.FromResult(Sse(Delta + tail)));
        var events = new List<StreamingSpeechPart>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in provider.SpeechStreamingAsync(Streaming()))
                events.Add(item);
        });
        Assert.DoesNotContain(events, p => p is SpeechFinishPart);
    }

    [Fact]
    public async Task Speech_done_without_audio_is_not_successful()
    {
        var provider = Create((_, _) => Task.FromResult(Sse(Done)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(provider.SpeechStreamingAsync(Streaming())));
    }

    [Theory]
    [InlineData("Text/Event-Stream", true)]
    [InlineData("application/json", false)]
    public async Task Speech_stream_requires_SSE_but_accepts_case_insensitive_HTTP_media_types(string mime, bool success)
    {
        var response = Response(new MemoryStream(Encoding.UTF8.GetBytes(Delta + Done)), mime);
        var provider = Create((_, _) => Task.FromResult(response));
        if (success)
            Assert.Single((await Drain(provider.SpeechStreamingAsync(Streaming()))).OfType<SpeechFinishPart>());
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(provider.SpeechStreamingAsync(Streaming())));
    }

    [Fact]
    public async Task Cancelling_after_raw_audio_does_not_forward_pending_audio_or_finish()
    {
        var stream = new PushStream();
        stream.Push(Delta + Done);
        var provider = Create((_, _) => Task.FromResult(Sse(stream)));
        var request = Streaming();
        request.IncludeRawChunks = true;
        using var cts = new CancellationTokenSource();
        await using var iterator = provider.SpeechStreamingAsync(request, cts.Token).GetAsyncEnumerator();
        while (await Next(iterator) && iterator.Current is not SpeechRawPart) { }
        Assert.IsType<SpeechRawPart>(iterator.Current);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Cancellation_and_consumer_disposal_close_the_native_response()
    {
        foreach (var cancel in new[] { false, true })
        {
            var stream = new PushStream();
            stream.Push(Delta);
            var provider = Create((_, _) => Task.FromResult(Sse(stream)));
            using var cts = new CancellationTokenSource();
            await using (var iterator = provider.OpenAISpeechStreamingAsync(new()
            {
                Model = Model, Voice = "builtin:auto", Input = "Hello"
            }, cts.Token).GetAsyncEnumerator())
            {
                Assert.True(await Next(iterator));
                if (cancel)
                {
                    cts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
                }
            }
            Assert.True(stream.Disposed);
        }
    }

    [Fact]
    public async Task Text_parts_join_midword_but_voice_changes_use_native_segments()
    {
        var provider = Create(async (message, _) =>
        {
            var segments = (await Payload(message)).GetProperty("segments");
            Assert.Equal(2, segments.GetArrayLength());
            Assert.Equal("Hello", segments[0].GetProperty("input").GetString());
            Assert.Equal("builtin:bright-girl", segments[0].GetProperty("voice").GetString());
            Assert.Equal("Other", segments[1].GetProperty("input").GetString());
            Assert.True(segments[1].GetProperty("disable_style").GetBoolean());
            return Sse(Delta + Done);
        });
        var request = Streaming();
        request.Text =
        [
            new() { Text = "", Voice = "builtin:auto" },
            new() { Text = "Hel", Voice = "builtin:bright-girl" },
            new() { Text = "lo", Voice = "builtin:bright-girl" },
            new() { Text = "Other", ProviderOptions = Options(new { disable_style = true }) }
        ];
        await Drain(provider.SpeechStreamingAsync(request));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("pcm")]
    [InlineData("flac")]
    [InlineData("opus")]
    public async Task Unsupported_output_formats_are_not_silently_substituted(string format)
    {
        var provider = Create(NoHttp);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new()
        {
            Model = Model, Voice = "builtin:auto", Text = "Hello", OutputFormat = format
        }));
        var request = Streaming();
        request.OutputFormat = new() { Type = format };
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(request)));
    }

    [Fact]
    public async Task HTTP_errors_preserve_status_native_error_and_retry_after()
    {
        var provider = Create((_, _) =>
        {
            var response = Json("""{"error":{"code":"model_downloading","param":"model","message":"Preparing"}}""", HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(15));
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Drain(provider.SpeechStreamingAsync(Streaming())));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Contains("model_downloading", error.Message);
        Assert.Contains("Retry-After: 15", error.Message);
    }

    [Fact]
    public void Registration_configuration_and_key_mapping_follow_existing_provider_contracts()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-EdgeSpeak-Key"] = " secret ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("secret", resolver.Resolve("edgespeak"));
        Assert.Equal("X-EdgeSpeak-Key", HeaderApiKeyResolver.SupportedProviderHeaders["edgespeak"]);
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new ClientFactory(new HttpClient(new Handler(NoHttp))));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ProviderRegistry>().GetModelProvider(scope.ServiceProvider, "edgespeak");
        Assert.IsType<EdgeSpeakProvider>(provider);
        Assert.Same(provider, scope.ServiceProvider.GetRequiredService<EdgeSpeakProvider>());
    }

    private static StreamingSpeechRequest Streaming() => new()
    {
        Model = Model, Voice = "builtin:auto", Text = [new() { Text = "Hello" }]
    };

    private static Dictionary<string, JsonElement> Options(object value) => new() { ["edgespeak"] = JsonSerializer.SerializeToElement(value) };
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Binary(string mime = "audio/wav") => Response(new MemoryStream("RIFF"u8.ToArray()), mime);
    private static HttpResponseMessage Sse(string text) => Sse(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    private static HttpResponseMessage Sse(Stream stream) => Response(stream, "text/event-stream");
    private static HttpResponseMessage Response(Stream stream, string mime)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new(mime);
        return response;
    }
    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("127.0.0.1", message.RequestUri!.Host);
        Assert.Equal("Bearer", message.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", message.Headers.Authorization?.Parameter);
    }
    private static async Task<JsonElement> Payload(HttpRequestMessage message)
    {
        using var document = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream)
            items.Add(item);
        return items;
    }
    private static Task<bool> Next<T>(IAsyncEnumerator<T> iterator) => iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    private static Task<HttpResponseMessage> NoHttp(HttpRequestMessage _, CancellationToken __) => throw new Xunit.Sdk.XunitException("Unexpected HTTP request.");
    private static EdgeSpeakProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string? key = "test-key", AsyncCacheHelper? cache = null, string? endpoint = null)
        => new(new KeyResolver(key), cache ?? new(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(send))),
            Microsoft.Extensions.Options.Options.Create(new EdgeSpeakProviderOptions { Endpoint = endpoint ?? "http://127.0.0.1:1118/" }));
    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class PushStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;
        public bool Disposed { get; private set; }
        public void Push(string text) => _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_offset == _current.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(ct))
                    return 0;
                _current = await _channel.Reader.ReadAsync(ct);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
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
