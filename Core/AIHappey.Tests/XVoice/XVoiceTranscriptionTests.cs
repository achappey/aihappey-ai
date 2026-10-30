using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.XVoice;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.XVoice;

public sealed class XVoiceTranscriptionTests
{
    [Fact]
    public async Task Multipart_preserves_native_options_auth_and_dynamic_model()
    {
        var provider = CreateProvider(async (request, _) =>
        {
            Assert.Equal("/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("cadence-stt-v2", await parts["model"].ReadAsStringAsync());
            Assert.Equal("fr", await parts["language"].ReadAsStringAsync());
            Assert.Equal("names", await parts["prompt"].ReadAsStringAsync());
            Assert.Equal("true", await parts["native_future_option"].ReadAsStringAsync());
            Assert.Equal("audio/wav", parts["file"].Headers.ContentType!.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3 }, await parts["file"].ReadAsByteArrayAsync());
            return Response("""{"text":"bonjour","usage":{"type":"duration","seconds":1.25}}""");
        });
        var request = FileRequest();
        request.Model = "xvoice/cadence-stt-v2";
        request.Audio = "data:audio/wav;base64,AQID";
        request.ProviderOptions = Native(new { language = "fr", prompt = "names", native_future_option = true });
        var response = await provider.TranscriptionRequest(request);
        Assert.Equal("bonjour", response.Text);
        Assert.Equal(1.25f, response.DurationInSeconds);
        Assert.Equal("xvoice/cadence-stt-v2", response.Response.ModelId);
    }

    [Theory]
    [InlineData("audio/flac", ".flac")]
    [InlineData("audio/mp4", ".m4a")]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("audio/ogg", ".ogg")]
    [InlineData("audio/wav", ".wav")]
    [InlineData("audio/webm", ".webm")]
    public async Task Supported_audio_formats_are_uploaded_without_conversion(string mediaType, string extension)
    {
        var provider = CreateProvider((request, _) =>
        {
            var file = Assert.IsType<MultipartFormDataContent>(request.Content)
                .Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "file");
            Assert.EndsWith(extension, file.Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal(mediaType, file.Headers.ContentType!.MediaType);
            return Task.FromResult(Response("""{"text":"ok"}"""));
        });
        var request = FileRequest();
        request.MediaType = mediaType;
        Assert.Equal("ok", (await provider.TranscriptionRequest(request)).Text);
    }

    [Theory]
    [InlineData("verbose_json")]
    [InlineData("srt")]
    [InlineData("vtt")]
    [InlineData("diarized_json")]
    public async Task Unsupported_response_formats_are_rejected(string format)
    {
        var provider = NeverSendProvider();
        var request = FileRequest();
        request.ProviderOptions = Native(new { response_format = format });
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(request));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAITranscriptionRequestAsync(
            OpenAIRequest(format)));
    }

    [Fact]
    public async Task Text_response_is_not_parsed_as_json()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(Response("hello\nworld", "text/plain")));
        var response = await provider.OpenAITranscriptionRequestAsync(OpenAIRequest("text"));
        Assert.Equal("hello\nworld", response.Text);
    }

    [Fact]
    public async Task OpenAI_bridge_preserves_provider_options_and_duration_usage()
    {
        var provider = CreateProvider(async (request, _) =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("custom", await parts["native_parameter"].ReadAsStringAsync());
            Assert.Equal("de", await parts["language"].ReadAsStringAsync());
            Assert.Equal(1000, (await parts["prompt"].ReadAsStringAsync()).Length);
            return Response("""{"text":"hallo","usage":{"type":"duration","seconds":3.5}}""");
        });
        var request = OpenAIRequest();
        request.Language = "de";
        request.Prompt = new string('x', 1000);
        request.AdditionalProperties = new()
        {
            ["providerOptions"] = JsonSerializer.SerializeToElement(new
            {
                xvoice = new { native_parameter = "custom", language = "fr" }
            })
        };
        var response = Assert.IsType<OpenAITranscriptionResponse>(
            await provider.OpenAITranscriptionRequestAsync(request));
        Assert.Equal("hallo", response.Text);
        Assert.Equal(3.5, Assert.IsType<OpenAITranscriptionDurationUsage>(response.Usage).Seconds);
    }

    [Fact]
    public async Task File_request_can_aggregate_native_stream()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(Response(StreamBody, "text/event-stream")));
        var request = FileRequest();
        request.ProviderOptions = Native(new { stream = true });
        var response = await provider.TranscriptionRequest(request);
        Assert.Equal("hello", response.Text);
        Assert.Equal(2.5f, response.DurationInSeconds);
    }

    [Fact]
    public async Task Prompt_limit_and_invalid_audio_fail_before_sending()
    {
        var provider = NeverSendProvider();
        var request = FileRequest();
        request.ProviderOptions = Native(new { prompt = new string('x', 1001) });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.TranscriptionRequest(request));
        request.ProviderOptions = null;
        request.Audio = "not-base64";
        await Assert.ThrowsAsync<ArgumentException>(() => provider.TranscriptionRequest(request));
        request.Audio = "AQID";
        request.MediaType = "audio/pcm";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(request));
    }

    [Fact]
    public async Task File_size_limit_is_enforced_at_25_MiB()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(Response("""{"text":"ok"}""")));
        var request = FileRequest();
        request.Audio = Convert.ToBase64String(new byte[25 * 1024 * 1024]);
        Assert.Equal("ok", (await provider.TranscriptionRequest(request)).Text);
        request.Audio = Convert.ToBase64String(new byte[25 * 1024 * 1024 + 1]);
        await Assert.ThrowsAsync<ArgumentException>(() => NeverSendProvider().TranscriptionRequest(request));
    }

    [Fact]
    public async Task Native_stream_emits_deltas_then_full_text_and_duration_usage()
    {
        var provider = CreateProvider(async (request, _) =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            Assert.Equal("true", await form.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "stream").ReadAsStringAsync());
            return Response(StreamBody, "text/event-stream");
        });
        var events = new List<IOpenAITranscriptionStreamEvent>();
        await foreach (var item in provider.OpenAITranscriptionStreamingAsync(OpenAIRequest()))
            events.Add(item);
        Assert.Equal("hel", Assert.IsType<OpenAITranscriptionTextDelta>(events[0]).Delta);
        Assert.Equal("lo", Assert.IsType<OpenAITranscriptionTextDelta>(events[1]).Delta);
        var done = Assert.IsType<OpenAITranscriptionTextDone>(events[2]);
        Assert.Equal("hello", done.Text);
        Assert.Equal(2.5, Assert.IsType<JsonElement>(done.Usage).GetProperty("seconds").GetDouble());
    }

    [Fact]
    public async Task Stream_done_without_required_text_fails_instead_of_reporting_success()
    {
        var body = StreamBody.Replace("\"text\":\"hello\",", "");
        var provider = CreateProvider((_, _) => Task.FromResult(Response(body, "text/event-stream")));
        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var item in provider.OpenAITranscriptionStreamingAsync(OpenAIRequest()))
                Assert.IsNotType<OpenAITranscriptionTextDone>(item);
        });
    }

    [Fact]
    public async Task Unified_stream_accepts_complete_file_and_exposes_native_raw_chunks()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(Response(StreamBody, "text/event-stream")));
        var events = new List<StreamingTranscriptionPart>();
        await foreach (var item in provider.TranscriptionStreamingAsync(new()
        {
            Model = "cadence-stt", Audio = "AQID", InputAudioFormat = new() { Type = "wav" },
            IncludeRawChunks = true
        }))
            events.Add(item);
        Assert.Equal(3, events.OfType<TranscriptionRawPart>().Count());
        Assert.Equal("hello", string.Concat(events.OfType<TranscriptionDeltaPart>().Select(p => p.Delta)));
        var finish = Assert.Single(events.OfType<TranscriptionFinishPart>());
        Assert.Equal("hello", finish.Text);
        Assert.Equal(2.5, finish.DurationInSeconds);
    }

    [Fact]
    public async Task Raw_realtime_audio_is_explicitly_unsupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in NeverSendProvider().TranscriptionStreamingAsync(new()
            {
                Model = "cadence-stt", Audio = "AQID", InputAudioFormat = new() { Type = "pcm16", Rate = 16000 }
            })) { }
        });
    }

    [Theory]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"message\":\"bad audio\"}}\n\n")]
    [InlineData("data: {\"type\":\"transcript.text.delta\",\"delta\":\"partial\"}\n\n")]
    [InlineData("data: {\"type\":\"transcript.text.delta\"}\n\n")]
    public async Task Error_or_truncated_native_stream_never_reports_success(string body)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(Response(body, "text/event-stream")));
        var completed = false;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in provider.OpenAITranscriptionStreamingAsync(OpenAIRequest()))
                completed |= item is OpenAITranscriptionTextDone;
        });
        Assert.False(completed);
    }

    [Fact]
    public async Task Http_errors_include_provider_response()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"audio exceeds 600 seconds"}""")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.TranscriptionRequest(FileRequest()));
        Assert.Contains("600", error.Message);
    }

    [Fact]
    public async Task Cancellation_reaches_http_transport()
    {
        using var source = new CancellationTokenSource();
        var provider = CreateProvider(async (_, token) =>
        {
            source.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Response("");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranscriptionRequest(FileRequest(), source.Token));
    }

    private const string StreamBody =
        "data: {\"type\":\"transcript.text.delta\",\"delta\":\"hel\"}\n\n"
        + "data: {\"type\":\"transcript.text.delta\",\"delta\":\"lo\"}\n\n"
        + "data: {\"type\":\"transcript.text.done\",\"text\":\"hello\",\"usage\":{\"type\":\"duration\",\"seconds\":2.5}}\n\n";

    private static TranscriptionRequest FileRequest() => new()
    {
        Model = "cadence-stt", Audio = "AQID", MediaType = "audio/wav"
    };

    private static Dictionary<string, JsonElement> Native(object options)
        => new() { ["xvoice"] = JsonSerializer.SerializeToElement(options) };

    private static OpenAITranscriptionRequest OpenAIRequest(string? format = null) => new()
    {
        Model = "cadence-stt", ResponseFormat = format,
        File = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(), ContentType = "audio/wav"
        }
    };

    private static HttpResponseMessage Response(string body, string contentType = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static XVoiceProvider NeverSendProvider()
        => CreateProvider((_, _) => throw new InvalidOperationException("Request should not be sent."));

    private static XVoiceProvider CreateProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Resolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new Factory(new HttpClient(new Handler(send)) { BaseAddress = new Uri("https://api.xvoice.dev/") }));

    private sealed class Resolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
