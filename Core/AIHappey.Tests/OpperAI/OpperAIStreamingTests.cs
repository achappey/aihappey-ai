using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpperAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpperAI;

public sealed class OpperAIStreamingTests
{
    private const string AudioModels = """
        {"models":[
          {"id":"mistral/voxtral-mini-2602","type":"stt","capabilities":{"streaming":true}},
          {"id":"openai/whisper-1","type":"stt","capabilities":{"streaming":false}}
        ]}
        """;

    [Fact]
    public void Capabilities_are_read_per_model()
    {
        using var doc = JsonDocument.Parse(AudioModels);
        var caps = OpperAIProvider.ParseOpperAIAudioStreamingCapabilities(doc.RootElement);

        Assert.True(caps["mistral/voxtral-mini-2602"]);
        Assert.False(caps["openai/whisper-1"]);
    }

    [Fact]
    public async Task Transcription_streams_sse_deltas_done_and_usage()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/audio/models")
                return Json(AudioModels);

            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Sse("""
                data: {"type":"transcript.text.delta","delta":"Hel"}

                data: {"type":"transcript.text.delta","delta":"lo"}

                data: {"type":"transcript.text.done","text":"Hello","usage":{"seconds":2.5,"cost":0.001}}

                data: [DONE]

                """);
        });

        var parts = await Collect(provider.TranscriptionStreamingAsync(new StreamingTranscriptionRequest
        {
            Model = "mistral/voxtral-mini-2602",
            Audio = Convert.ToBase64String([1, 2, 3]),
            InputAudioFormat = new AudioFormat { Type = "mp3" }
        }));

        using var doc = JsonDocument.Parse(body!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.StartsWith("data:audio/mpeg;base64,", doc.RootElement.GetProperty("audio").GetString());
        Assert.Equal(["Hel", "lo"], parts.OfType<TranscriptionDeltaPart>().Select(p => p.Delta));
        Assert.Equal("Hello", parts.OfType<TranscriptionFinalPart>().Single().Text);

        var finish = parts.OfType<TranscriptionFinishPart>().Single();
        Assert.Equal(2.5, finish.DurationInSeconds);
        Assert.Equal(0.001, finish.ProviderMetadata!["gateway"].GetProperty("cost").GetDouble());
    }

    [Fact]
    public async Task Transcription_uses_buffered_request_when_model_does_not_stream()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/audio/models")
                return Json(AudioModels);

            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"text":"Whole","language":"en","duration":1.5,"usage":{"seconds":1.5,"cost":0.002}}""");
        });

        var parts = await Collect(provider.TranscriptionStreamingAsync(new StreamingTranscriptionRequest
        {
            Model = "openai/whisper-1",
            Audio = Convert.ToBase64String([1]),
            InputAudioFormat = new AudioFormat { Type = "wav" }
        }));

        Assert.DoesNotContain("\"stream\"", body);
        Assert.Equal("Whole", parts.OfType<TranscriptionDeltaPart>().Single().Delta);
        Assert.Equal("en", parts.OfType<TranscriptionFinishPart>().Single().Language);
    }

    [Fact]
    public async Task Transcription_preserves_http_and_stream_errors()
    {
        var failing = CreateProvider(request => request.RequestUri!.AbsolutePath == "/v3/audio/models"
            ? Json(AudioModels)
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"nope"}}""") });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(failing.TranscriptionStreamingAsync(TranscribeRequest())));
        Assert.Contains("400", ex.Message);
        Assert.Contains("nope", ex.Message);

        var errorEvent = CreateProvider(request => request.RequestUri!.AbsolutePath == "/v3/audio/models"
            ? Json(AudioModels)
            : Sse("data: {\"type\":\"error\",\"error\":{\"message\":\"boom\"}}\n\n"));

        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(errorEvent.TranscriptionStreamingAsync(TranscribeRequest())));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Transcription_honors_cancellation()
    {
        var provider = CreateProvider(_ => Json(AudioModels));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider.TranscriptionStreamingAsync(TranscribeRequest(), cts.Token)));
    }

    [Fact]
    public async Task Speech_falls_back_to_buffered_response_and_preserves_voice_format_and_cost()
    {
        var audio = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var bodies = new List<string>();
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v3/audio/speech", request.RequestUri!.AbsolutePath);
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json("{\"model\":\"tts-1\",\"audio\":{\"b64_json\":\"" + Convert.ToBase64String(audio) + "\",\"mime_type\":\"audio/mpeg\"},\"usage\":{\"characters\":5,\"cost\":0.01}}");
        });

        var parts = await Collect(provider.SpeechStreamingAsync(new StreamingSpeechRequest
        {
            Model = "openai/tts-1",
            Voice = "alloy",
            OutputFormat = new AudioFormat { Type = "mp3" },
            Text = [new() { Text = "Hel" }, new() { Text = "lo" }]
        }));

        using var doc = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal("Hello", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("alloy", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("mp3", doc.RootElement.GetProperty("format").GetString());
        Assert.False(doc.RootElement.GetProperty("store").GetBoolean());

        var chunks = parts.OfType<SpeechAudioPart>().ToList();
        Assert.True(chunks.Count > 1);
        Assert.Equal(audio, chunks.SelectMany(c => Convert.FromBase64String(c.Audio)).ToArray());
        Assert.All(chunks, c => Assert.Equal("mp3", c.AudioFormat.Type));

        var finish = parts.OfType<SpeechFinishPart>().Single();
        Assert.Equal(5, finish.Usage!.InputCharacters);
        Assert.Equal(0.01, finish.ProviderMetadata!["gateway"].GetProperty("cost").GetDouble());
        Assert.IsType<SpeechFinishPart>(parts[^1]);
    }

    [Fact]
    public async Task Speech_splits_requests_when_voice_changes_and_propagates_errors()
    {
        var voices = new List<string?>();
        var provider = CreateProvider(request =>
        {
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            voices.Add(doc.RootElement.GetProperty("voice").GetString());
            return Json("""{"audio":{"b64_json":"AQID"},"usage":{"characters":1,"cost":0.5}}""");
        });

        var parts = await Collect(provider.SpeechStreamingAsync(new StreamingSpeechRequest
        {
            Model = "m",
            Voice = "a",
            Text = [new() { Text = "x" }, new() { Text = "y", Voice = "b" }]
        }));

        Assert.Equal(["a", "b"], voices);
        Assert.Equal(1.0, parts.OfType<SpeechFinishPart>().Single().ProviderMetadata!["gateway"].GetProperty("cost").GetDouble());

        var failing = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad voice") });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(failing.SpeechStreamingAsync(new StreamingSpeechRequest
        {
            Model = "m",
            Text = [new() { Text = "x" }]
        })));
        Assert.Contains("bad voice", ex.Message);
    }

    private static StreamingTranscriptionRequest TranscribeRequest() => new()
    {
        Model = "mistral/voxtral-mini-2602",
        Audio = Convert.ToBase64String([1]),
        InputAudioFormat = new AudioFormat { Type = "mp3" }
    };

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static OpperAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(string.Join('\n', body.Split('\n').Select(l => l.TrimStart())), Encoding.UTF8, "text/event-stream") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
