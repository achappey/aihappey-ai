using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AIHappey.Tests.EdgeSpeak;

public sealed partial class EdgeSpeakProviderTests
{
    private const string Transcript = """
        {"task":"transcribe","text":"Hello world","language":"en","duration":1.5,
         "usage":{"type":"duration","seconds":1.5},
         "segments":[{"id":0,"type":"segment","text":"Hello world","start":0.1,"end":1.2,"speaker":"speaker_1","avg_logprob":-0.1,
                      "words":[{"word":"Hello","start":0.1,"end":0.5,"score":0.99},{"word":"world","start":0.6,"end":1.2,"score":0.9}],
                      "x_edgespeak":{"candidates":[{"text":"Hallo world"}]}}]}
        """;
    private const string TranscriptDelta = "event: transcript.text.delta\ndata: {\"type\":\"transcript.text.delta\",\"delta\":\"Hello\"}\n\n";
    private const string TranscriptDone = "event: transcript.text.done\ndata: {\"type\":\"transcript.text.done\",\"text\":\"Hello world\",\"usage\":{\"type\":\"duration\",\"seconds\":1.5}}\n\n";

    [Fact]
    public async Task Transcription_preserves_all_native_fields_diarization_words_and_metadata()
    {
        var provider = Create(async (message, ct) =>
        {
            AssertAuth(message);
            Assert.Equal("/v1/audio/transcriptions", message.RequestUri!.AbsolutePath);
            var form = await ReadForm(message, ct);
            Assert.Equal("transcriber/model", form["model"]);
            Assert.Equal("diarized_json", form["response_format"]);
            Assert.Equal("false", form["stream"]);
            Assert.Equal("speaker/model", form["diarization_model"]);
            Assert.Equal("en-US", form["language"]);
            Assert.Equal("Context", form["prompt"]);
            Assert.Equal(new[] { "word", "segment" }, form["timestamp_granularities[]"].ToArray());
            Assert.Equal("true", form["word_timestamp_enabled"]);
            Assert.Equal("true", form["semantic_sentence_enabled"]);
            Assert.Equal("10", form["min_chars"]);
            Assert.Equal("80", form["max_chars"]);
            Assert.Equal("0.1", form["start_margin"]);
            Assert.Equal("0.2", form["end_margin"]);
            Assert.Equal("1", form["candidates"]);
            Assert.Equal("audio.wav", Assert.Single(form.Files).FileName);
            Assert.Equal("audio/wav", Assert.Single(form.Files).ContentType);
            return Json(Transcript);
        });
        var response = await provider.TranscriptionRequest(new()
        {
            Model = "edgespeak/transcriber/model", Audio = "data:audio/wav;base64,UklGRg==", MediaType = "audio/wav",
            ProviderOptions = Options(new
            {
                response_format = "diarized_json", diarization_model = "speaker/model", language = "en-US",
                prompt = "Context", timestamp_granularities = new[] { "word", "segment" }, word_timestamp_enabled = true,
                semantic_sentence_enabled = true, min_chars = 10, max_chars = 80, start_margin = 0.1, end_margin = 0.2, candidates = 1
            })
        });
        Assert.Equal("Hello world", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal(1.5f, response.DurationInSeconds);
        Assert.Equal(0.1f, Assert.Single(response.Segments).StartSecond);
        Assert.Equal("speaker_1", response.ProviderMetadata!["edgespeak"].GetProperty("segments")[0].GetProperty("speaker").GetString());
        Assert.Equal(2, response.ProviderMetadata["edgespeak"].GetProperty("segments")[0].GetProperty("words").GetArrayLength());
        Assert.NotNull(response.Response.Body);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("verbose_json")]
    [InlineData("diarized_json")]
    [InlineData("text")]
    public async Task OpenAI_transcription_formats_preserve_available_contract_fields(string format)
    {
        var provider = Create(async (message, ct) =>
        {
            Assert.Equal(format, (await ReadForm(message, ct))["response_format"]);
            return format == "text"
                ? new(System.Net.HttpStatusCode.OK) { Content = new StringContent("Hello world", Encoding.UTF8, "text/plain") }
                : Json(Transcript);
        });
        var response = await provider.OpenAITranscriptionRequestAsync(new()
        {
            Model = "transcriber/model", File = File(), ResponseFormat = format
        });
        Assert.Equal("Hello world", response.Text);
        if (response is OpenAITranscriptionVerboseResponse verbose)
        {
            Assert.Equal(2, verbose.Words!.Length);
            Assert.Equal("Hello", verbose.Words[0].Word);
            Assert.Equal(0.1, verbose.Words[0].Start);
            Assert.Equal(1.5, verbose.Duration);
            Assert.IsType<OpenAITranscriptionDurationUsage>(verbose.Usage);
        }
        if (response is OpenAITranscriptionDiarizedResponse diarized)
            Assert.Equal("speaker_1", Assert.Single(diarized.Segments).Speaker);
    }

    [Fact]
    public async Task OpenAI_native_extensions_are_not_lost_during_request_mapping()
    {
        var provider = Create(async (message, ct) =>
        {
            var form = await ReadForm(message, ct);
            Assert.Equal("4", form["candidates"]);
            Assert.Equal("fr", form["language"]);
            Assert.Equal("Context", form["prompt"]);
            return Json(Transcript);
        });
        await provider.OpenAITranscriptionRequestAsync(new()
        {
            Model = "transcriber/model", File = File(), ResponseFormat = "verbose_json", Language = "fr", Prompt = "Context",
            AdditionalProperties = new() { ["edgespeak"] = JsonSerializer.SerializeToElement(new { candidates = 4 }) }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transcription_SSE_is_incremental_on_both_contracts(bool openAI)
    {
        var stream = new PushStream();
        stream.Push(": keepalive\n\n" + TranscriptDelta);
        var provider = Create(async (message, ct) =>
        {
            var form = await ReadForm(message, ct);
            Assert.Equal("true", form["stream"]);
            Assert.Equal("json", form["response_format"]);
            return Sse(stream);
        });
        if (openAI)
        {
            await using var iterator = provider.OpenAITranscriptionStreamingAsync(new()
            {
                Model = "transcriber/model", File = File(), Stream = true
            }).GetAsyncEnumerator();
            Assert.True(await Next(iterator));
            Assert.Equal("Hello", Assert.IsType<OpenAITranscriptionTextDelta>(iterator.Current).Delta);
            stream.Push(TranscriptDone);
            Assert.True(await Next(iterator));
            Assert.Equal("Hello world", Assert.IsType<OpenAITranscriptionTextDone>(iterator.Current).Text);
            Assert.False(await Next(iterator));
        }
        else
        {
            await using var iterator = provider.TranscriptionStreamingAsync(StreamingTranscript()).GetAsyncEnumerator();
            while (await Next(iterator) && iterator.Current is not TranscriptionDeltaPart) { }
            Assert.Equal("Hello", Assert.IsType<TranscriptionDeltaPart>(iterator.Current).Delta);
            Assert.False(stream.Disposed);
            stream.Push(TranscriptDone);
            while (await Next(iterator) && iterator.Current is not TranscriptionFinishPart) { }
            Assert.Equal("Hello world", Assert.IsType<TranscriptionFinishPart>(iterator.Current).Text);
            Assert.False(await Next(iterator));
        }
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("event: transcript.text.done\ndata: {\"text\":\"Hello\"}")]
    [InlineData("event: error\ndata: {\"error\":{\"code\":\"failed\"}}\n\n")]
    public async Task Transcription_disconnects_and_errors_do_not_finish(string tail)
    {
        var provider = Create((_, _) => Task.FromResult(Sse(TranscriptDelta + tail)));
        var seen = new List<StreamingTranscriptionPart>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in provider.TranscriptionStreamingAsync(StreamingTranscript()))
                seen.Add(item);
        });
        Assert.DoesNotContain(seen, x => x is TranscriptionFinishPart);
    }

    [Fact]
    public async Task Streaming_detailed_transcription_options_are_rejected_without_HTTP()
    {
        foreach (var options in new object[]
        {
            new { response_format = "verbose_json" },
            new { response_format = "diarized_json" },
            new { timestamp_granularities = new[] { "word" } },
            new Dictionary<string, object> { ["timestamp_granularities[]"] = new[] { "segment" } },
            new { word_timestamp_enabled = true },
            new { diarization_model = "diarization/model" }
        })
        {
            var request = StreamingTranscript();
            request.ProviderOptions = Options(options);
            await Assert.ThrowsAsync<NotSupportedException>(() => Drain(Create(NoHttp).TranscriptionStreamingAsync(request)));
        }
    }

    [Fact]
    public async Task Transcription_cancellation_closes_an_active_SSE_response()
    {
        var stream = new PushStream();
        stream.Push(TranscriptDelta);
        var provider = Create((_, _) => Task.FromResult(Sse(stream)));
        using var cts = new CancellationTokenSource();
        await using var iterator = provider.OpenAITranscriptionStreamingAsync(new()
        {
            Model = "transcriber/model", File = File()
        }, cts.Token).GetAsyncEnumerator();
        Assert.True(await Next(iterator));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        Assert.True(stream.Disposed);
    }

    private static StreamingTranscriptionRequest StreamingTranscript() => new()
    {
        Model = "transcriber/model", Audio = "UklGRg==", InputAudioFormat = new() { Type = "audio/wav" }
    };

    private static IFormFile File()
        => new FormFile(new MemoryStream("RIFF"u8.ToArray()), 0, 4, "file", "audio.wav")
        { Headers = new HeaderDictionary(), ContentType = "audio/wav" };

    private static async Task<IFormCollection> ReadForm(HttpRequestMessage message, CancellationToken ct)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = message.Content!.Headers.ContentType!.ToString();
        context.Request.Body = new MemoryStream(await message.Content.ReadAsByteArrayAsync(ct));
        return await context.Request.ReadFormAsync(ct);
    }
}
