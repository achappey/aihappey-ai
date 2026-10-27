using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Mistral;

public sealed class MistralAudioStreamingTests
{
    public enum Adapter { VercelSpeech, OpenAISpeech, VercelTranscription, OpenAITranscription }

    [Fact]
    public async Task VercelSpeechPreservesTextBoundariesFormatOptionsRawEventsAndUsage()
    {
        JsonElement sent = default;
        var provider = Create(async message =>
        {
            AssertRequest(message, "/v1/audio/speech");
            sent = JsonDocument.Parse(await message.Content!.ReadAsStringAsync()).RootElement.Clone();
            var response = Sse(SpeechDelta("AQI=") + SpeechDelta("AwQ=") + Event("speech.audio.done",
                """{"usage":{"input_characters":12,"input_tokens":7,"output_tokens":9,"total_tokens":16,"extra":"kept"}}"""));
            response.Headers.Add("x-request-id", "speech-request");
            return response;
        });
        var request = SpeechRequest();
        request.Text = [new() { Text = "hel" }, new() { Text = "lo" }, new() { Text = " \nworld" }];
        request.OutputFormat = new() { Type = "audio/wav", Rate = 24000 };
        request.Instructions = "calm";
        request.Speed = 1.2;
        request.Language = "en";
        request.IncludeRawChunks = true;
        request.ProviderOptions = Options("""{"response_format":"mp3","input":"wrong","model":"wrong","stream":false,"phoneme_hints":{"hello":["h","e"]},"prompt_cache_key":"cache"}""");
        var before = JsonSerializer.Serialize(request);

        var parts = await Collect(provider.SpeechStreamingAsync(request));

        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("hello \nworld", sent.GetProperty("input").GetString());
        Assert.Equal("voxtral-mini-tts-3-0", sent.GetProperty("model").GetString());
        Assert.True(sent.GetProperty("stream").GetBoolean());
        Assert.Equal("wav", sent.GetProperty("response_format").GetString());
        Assert.Equal("cache", sent.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("h", sent.GetProperty("phoneme_hints").GetProperty("hello")[0].GetString());
        Assert.Equal(4, Assert.IsType<SpeechStreamStartPart>(parts[0]).Warnings.Count());
        var responseMetadata = Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        Assert.Equal("mistral/voxtral-mini-tts-3-0", responseMetadata.ModelId);
        Assert.Equal("speech-request", responseMetadata.Headers!["x-request-id"]);
        var chunks = parts.OfType<SpeechAudioPart>().ToList();
        Assert.Equal(["AQI=", "AwQ="], chunks.Select(p => p.Audio));
        Assert.All(chunks, p => { Assert.Equal("audio/wav", p.AudioFormat.Type); Assert.Null(p.AudioFormat.Rate); });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunks.SelectMany(p => Convert.FromBase64String(p.Audio)));
        Assert.Equal(3, parts.OfType<SpeechRawPart>().Count());
        var finish = Assert.IsType<SpeechFinishPart>(parts[^1]);
        Assert.Equal(12L, finish.Usage!.InputCharacters);
        Assert.Equal(7L, finish.Usage.InputTokens);
        Assert.Equal(9L, finish.Usage.OutputTokens);
        Assert.Equal("kept", finish.Usage.Raw!["extra"].GetString());
        Assert.Equal(16, finish.ProviderMetadata!["mistral"].GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VercelSpeechRejectsMixedEffectiveSettingsBeforeAnyNetworkCall(bool options)
    {
        var calls = 0;
        var provider = Create(_ => { calls++; return Task.FromResult(Sse("")); });
        var request = SpeechRequest();
        request.Text = options
            ? [new() { Text = "a", ProviderOptions = Options("""{"prompt_cache_key":"one"}""") },
               new() { Text = "b", ProviderOptions = Options("""{"prompt_cache_key":"two"}""") }]
            : [new() { Text = "a", Voice = "alice" }, new() { Text = "b", Voice = "bob" }];
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(provider.SpeechStreamingAsync(request)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task VercelSpeechMergesEquivalentPartOptionsAndPreservesVoiceShortcutSelection()
    {
        JsonElement sent = default;
        var provider = Create(async message =>
        {
            if (message.RequestUri!.AbsolutePath == "/v1/audio/voices")
                return Json("""{"items":[{"id":"voice-123","slug":"jane"}],"total":1}""");
            sent = JsonDocument.Parse(await message.Content!.ReadAsStringAsync()).RootElement.Clone();
            return Sse(SpeechDelta("AQ==") + SpeechDone());
        });
        var request = SpeechRequest();
        request.Model += "/jane";
        request.Voice = "ignored";
        request.ProviderOptions = Options("""{"prompt_cache_key":"cache","metadata":{"a":1,"b":2}}""");
        request.Text =
        [
            new() { Text = "a", ProviderOptions = Options("""{"metadata":{"b":2,"a":1}}""") },
            new() { Text = "b" }
        ];
        var parts = await Collect(provider.SpeechStreamingAsync(request));
        Assert.Equal("voice-123", sent.GetProperty("voice_id").GetString());
        Assert.Equal("voxtral-mini-tts-3-0", sent.GetProperty("model").GetString());
        Assert.Equal("ab", sent.GetProperty("input").GetString());
        Assert.Contains(Assert.Single(parts.OfType<SpeechStreamStartPart>()).Warnings,
            w => JsonSerializer.SerializeToElement(w).GetProperty("type").GetString() == "ignored");
    }

    [Fact]
    public async Task VercelTranscriptionPreservesVerbatimDeltasOrderedSegmentsAndAuthoritativeFinal()
    {
        Dictionary<string, List<string>>? fields = null;
        var provider = Create(async message =>
        {
            AssertRequest(message, "/v1/audio/transcriptions");
            fields = await ReadForm(message);
            return Sse(
                Event("transcription.language", """{"language":"en"}""") +
                TextDelta("Hello") + TextDelta(" \n") + TextDelta("world") +
                Segment("late", 4.25, 6.75, "speaker-2") + Segment(" early", 1.5, 2.5, "speaker-1") +
                Segment(" untimed", null, null, "speaker-3") +
                Event("transcription.done", """{"text":"Corrected final \n","model":"voxtral-final","language":null,"segments":[{"text":"late corrected","start":4.25,"end":6.75,"speaker_id":"speaker-2"},{"text":" early","start":1.5,"end":2.5,"speaker_id":"speaker-1"},{"text":" untimed","start":null,"end":null,"speaker_id":"speaker-3"}],"usage":{"prompt_audio_seconds":34,"prompt_tokens":3},"finish_reason":"stop"}"""));
        });
        var request = TranscriptionRequest();
        request.IncludeRawChunks = true;
        request.ProviderOptions = Options("""{"language":"fr","timestamp_granularities":["segment"],"context_bias":["Northwind","Quilliera"],"diarize":true,"return_language":true,"temperature":0.2,"model":"wrong","stream":false,"file_url":"https://wrong","file_id":"wrong","file":"wrong"}""");
        var before = JsonSerializer.Serialize(request);

        var parts = await Collect(provider.TranscriptionStreamingAsync(request));

        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("voxtral-mini-transcribe-3", Assert.Single(fields!["model"]));
        Assert.Equal("true", Assert.Single(fields["stream"]));
        Assert.Equal("fr", Assert.Single(fields["language"]));
        Assert.Equal(["segment"], fields["timestamp_granularities"]);
        Assert.Equal(["Northwind", "Quilliera"], fields["context_bias"]);
        Assert.Equal("true", Assert.Single(fields["diarize"]));
        Assert.Equal("true", Assert.Single(fields["return_language"]));
        Assert.Equal("0.2", Assert.Single(fields["temperature"]));
        Assert.False(fields.ContainsKey("file_url"));
        Assert.False(fields.ContainsKey("file_id"));
        Assert.Equal(["Hello", " \n", "world"], parts.OfType<TranscriptionDeltaPart>().Select(p => p.Delta));
        var segments = parts.OfType<TranscriptionFinalPart>().ToList();
        Assert.Equal(["late", " early", " untimed"], segments.Select(p => p.Text));
        Assert.Equal(4.25, segments[0].StartSecond);
        Assert.Equal(6.75, segments[0].EndSecond);
        Assert.Equal(1.5, segments[1].StartSecond);
        Assert.Null(segments[2].StartSecond);
        Assert.Null(segments[2].EndSecond);
        Assert.All(segments, p => Assert.Null(p.ChannelIndex));
        Assert.Equal("speaker-2", segments[0].ProviderMetadata!["mistral"].GetProperty("speaker_id").GetString());
        var finish = Assert.IsType<TranscriptionFinishPart>(parts[^1]);
        Assert.Equal("Corrected final \n", finish.Text);
        Assert.Null(finish.Language); // Final null is authoritative, not the earlier language.
        Assert.Equal(34, finish.DurationInSeconds);
        Assert.Equal(["late corrected", " early"], finish.Segments.Select(p => p.Text));
        Assert.Equal([4.25f, 1.5f], finish.Segments.Select(p => p.StartSecond));
        var native = finish.ProviderMetadata!["mistral"];
        Assert.Equal(3, native.GetProperty("segments").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, native.GetProperty("segments")[2].GetProperty("start").ValueKind);
        Assert.Equal("speaker-3", native.GetProperty("segments")[2].GetProperty("speaker_id").GetString());
        Assert.Equal("stop", native.GetProperty("finish_reason").GetString());
        Assert.Equal(34, native.GetProperty("usage").GetProperty("prompt_audio_seconds").GetInt32());
        Assert.Equal(8, parts.OfType<TranscriptionRawPart>().Count());
        Assert.Equal("mistral/voxtral-final", parts.OfType<TranscriptionResponseMetadataPart>().Last().ModelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VercelTranscriptionDistinguishesMissingAndEmptyFinalSegments(bool empty)
    {
        var done = empty ? """{"text":"final","segments":[]}""" : """{"text":"final"}""";
        var provider = Create(_ => Task.FromResult(Sse(Event("transcription.language", """{"language":"en"}""") +
            Segment(" kept", 1, 2, "alice") + Event("transcription.done", done))));
        var parts = await Collect(provider.TranscriptionStreamingAsync(TranscriptionRequest()));
        var finish = Assert.Single(parts.OfType<TranscriptionFinishPart>());
        Assert.Equal("en", finish.Language);
        Assert.Equal(empty ? 0 : 1, finish.Segments.Count());
        Assert.Equal(empty ? 0 : 1, finish.ProviderMetadata!["mistral"].GetProperty("segments").GetArrayLength());
        Assert.Empty(parts.OfType<TranscriptionRawPart>());
    }

    [Fact]
    public async Task OpenAIAdaptersPreserveUsageWhitespaceFinalCorrectionsAndDiarizedSegments()
    {
        var speech = await Collect(AllParts(Create(_ => Task.FromResult(Sse(SpeechDelta("AQ==") +
            Event("speech.audio.done", """{"usage":{"prompt_tokens":2,"completion_tokens":4,"total_tokens":6}}""")))), Adapter.OpenAISpeech));
        Assert.Equal("AQ==", Assert.Single(speech.OfType<AudioSpeechStreamDelta>()).Audio);
        var usage = Assert.Single(speech.OfType<AudioSpeechStreamDone>()).Usage!;
        Assert.Equal(2, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(6, usage.TotalTokens);

        var transcription = await Collect(AllParts(Create(_ => Task.FromResult(Sse(TextDelta("draft") + TextDelta(" \n") +
            Segment("draft", 1.125, 2.875, "alice") + Event("transcription.done", """{"text":"final corrected","usage":{"prompt_audio_seconds":3}}""")))), Adapter.OpenAITranscription));
        Assert.Equal(["draft", " \n"], transcription.OfType<OpenAITranscriptionTextDelta>().Select(p => p.Delta));
        var segment = Assert.Single(transcription.OfType<OpenAITranscriptionTextSegment>());
        Assert.Equal("alice", segment.Speaker);
        Assert.Equal(1.125, segment.Start);
        Assert.Equal(2.875, segment.End);
        var done = Assert.Single(transcription.OfType<OpenAITranscriptionTextDone>());
        Assert.Equal("final corrected", done.Text);
        Assert.Equal(3, Assert.IsType<JsonElement>(done.Usage).GetProperty("prompt_audio_seconds").GetInt32());
    }

    [Theory]
    [InlineData(Adapter.VercelSpeech)]
    [InlineData(Adapter.OpenAISpeech)]
    [InlineData(Adapter.VercelTranscription)]
    [InlineData(Adapter.OpenAITranscription)]
    public async Task AllAdaptersParseMultilineFramesEventNamesCommentsAndUnterminatedFinalFrame(Adapter adapter)
    {
        var speech = IsSpeech(adapter);
        var delta = speech
            ? "event: speech.audio.delta\r\ndata: {\r\ndata: \"audio_data\":\"AQ==\"}\r\n\r\n"
            : "event: transcription.text.delta\r\ndata: {\r\ndata: \"text\":\" \\n\"}\r\n\r\n";
        var done = speech
            ? "event: speech.audio.done\ndata: {}"
            : "event: transcription.done\ndata: {\"text\":\"\"}";
        var parts = await Collect(AllParts(Create(_ => Task.FromResult(Sse(": heartbeat\r\nid: 1\r\nretry: 2\r\n\r\n" + delta + done))), adapter));
        Assert.Single(parts, IsFinish);
        if (!speech)
            Assert.Equal(" \n", Delta(parts.Single(p => p is TranscriptionDeltaPart or OpenAITranscriptionTextDelta)));
        if (adapter == Adapter.VercelSpeech)
        {
            Assert.Null(Assert.Single(parts.OfType<SpeechFinishPart>()).Usage);
            Assert.Equal("audio/mpeg", Assert.Single(parts.OfType<SpeechAudioPart>()).AudioFormat.Type);
            Assert.Empty(parts.OfType<SpeechRawPart>());
        }
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (var adapter in Enum.GetValues<Adapter>())
        foreach (var failure in new[] { "empty", "eof", "sentinel", "error-event", "error-object", "bad-json", "bad-payload", "bad-audio", "mismatched-type" })
            yield return [adapter, failure];
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task AllAdaptersFailExplicitlyWithoutSuccessfulCompletion(Adapter adapter, string failure)
    {
        var speech = IsSpeech(adapter);
        var prefix = speech ? SpeechDelta("AQ==") : TextDelta("partial");
        var body = failure switch
        {
            "empty" => "",
            "eof" => prefix,
            "sentinel" => prefix + "data: [DONE]\n\n",
            "error-event" => prefix + "event: error\ndata: upstream failed\n\n",
            "error-object" => prefix + "data: {\"error\":{\"message\":\"upstream failed\"}}\n\n",
            "bad-json" => prefix + "data: {bad}\n\n",
            "bad-payload" => prefix + Event(speech ? "speech.audio.delta" : "transcription.text.delta", "{}"),
            "bad-audio" => prefix + (speech ? SpeechDelta("invalid!") : Event("transcription.segment.delta", """{"text":"x","start":"invalid","end":2}""")),
            _ => prefix + "event: speech.audio.done\ndata: {\"type\":\"transcription.done\",\"text\":\"x\"}\n\n"
        };
        var parts = new List<object>();
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var part in AllParts(Create(_ => Task.FromResult(Sse(body))), adapter)) parts.Add(part);
        });
        Assert.True(error is InvalidOperationException or JsonException or FormatException, error?.ToString() ?? "No failure");
        Assert.DoesNotContain(parts, IsFinish);
    }

    [Theory]
    [InlineData(Adapter.VercelSpeech)]
    [InlineData(Adapter.OpenAISpeech)]
    [InlineData(Adapter.VercelTranscription)]
    [InlineData(Adapter.OpenAITranscription)]
    public async Task AllAdaptersDeliverFirstChunkBeforeCompletionThenStopWithoutWaitingForEof(Adapter adapter)
    {
        using var stream = new GatedStream(IsSpeech(adapter) ? SpeechDelta("AQ==") : TextDelta("first"), Done(adapter));
        var provider = Create(_ => Task.FromResult(Sse(stream)));
        await using var iterator = AllParts(provider, adapter).GetAsyncEnumerator();
        object? first = null;
        while (await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            if (iterator.Current is SpeechAudioPart or AudioSpeechStreamDelta or TranscriptionDeltaPart or OpenAITranscriptionTextDelta)
            {
                first = iterator.Current;
                break;
            }
        Assert.NotNull(first);
        Assert.False(stream.Released);
        stream.Release();
        var remaining = new List<object>();
        while (await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))) remaining.Add(iterator.Current);
        Assert.Single(remaining, IsFinish);
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.ReadsAfterSuffix); // Done closes the stream without another read.
    }

    [Theory]
    [InlineData(Adapter.VercelSpeech)]
    [InlineData(Adapter.OpenAISpeech)]
    [InlineData(Adapter.VercelTranscription)]
    [InlineData(Adapter.OpenAITranscription)]
    public async Task AllAdaptersHonorBlockedReadCancellationAndDisposeWithoutFinish(Adapter adapter)
    {
        using var stream = new GatedStream(IsSpeech(adapter) ? SpeechDelta("AQ==") : TextDelta("first"), Done(adapter));
        using var cancellation = new CancellationTokenSource();
        var provider = Create(_ => Task.FromResult(Sse(stream)));
        var parts = new List<object>();
        var reading = Task.Run(async () =>
        {
            await foreach (var part in AllParts(provider, adapter, cancellation.Token)) parts.Add(part);
        });
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(parts, IsFinish);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(Adapter.VercelSpeech)]
    [InlineData(Adapter.OpenAISpeech)]
    [InlineData(Adapter.VercelTranscription)]
    [InlineData(Adapter.OpenAITranscription)]
    public async Task AllAdaptersHonorPreCancellationAndEarlyConsumerDisposal(Adapter adapter)
    {
        var calls = 0;
        using var stream = new GatedStream(IsSpeech(adapter) ? SpeechDelta("AQ==") : TextDelta("first"), Done(adapter));
        var provider = Create(_ => { calls++; return Task.FromResult(Sse(stream)); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(AllParts(provider, adapter, cancellation.Token)));
        Assert.Equal(0, calls);
        await using (var iterator = AllParts(provider, adapter).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.False(IsFinish(iterator.Current));
        }
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(Adapter.VercelSpeech)]
    [InlineData(Adapter.OpenAISpeech)]
    [InlineData(Adapter.VercelTranscription)]
    [InlineData(Adapter.OpenAITranscription)]
    public async Task AllAdaptersReportHttpAndNonSseProviderErrorsAndDisposeResponse(Adapter adapter)
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.OK })
        {
            using var stream = new GatedStream("{\"error\":\"quota\"}", "", blockAfterPrefix: false);
            var provider = Create(_ => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StreamContent(stream) { Headers = { ContentType = new("application/json") } }
            }));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(AllParts(provider, adapter)));
            Assert.Contains("quota", error.Message);
            Assert.True(stream.Disposed);
        }
    }

    [Fact]
    public async Task OpenAIStreamingRequestsForwardDocumentedOptionsWithoutOverridingTransportFields()
    {
        JsonElement speechPayload = default;
        Dictionary<string, List<string>>? transcriptionFields = null;
        var provider = Create(async message =>
        {
            if (message.RequestUri!.AbsolutePath == "/v1/audio/speech")
            {
                speechPayload = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync());
                return Sse(SpeechDone());
            }
            transcriptionFields = await ReadForm(message);
            return Sse(Event("transcription.done", """{"text":""}"""));
        });
        var speech = new AudioSpeechRequest
        {
            Model = "mistral/voxtral-mini-tts-3-0", Input = "real input", ResponseFormat = "flac",
            AdditionalProperties = new()
            {
                ["input"] = JsonSerializer.SerializeToElement("wrong"),
                ["stream"] = JsonSerializer.SerializeToElement(false),
                ["response_format"] = JsonSerializer.SerializeToElement("mp3"),
                ["ref_audio"] = JsonSerializer.SerializeToElement("reference")
            }
        };
        var speechBefore = JsonSerializer.Serialize(speech);
        await Collect(provider.OpenAISpeechStreamingAsync(speech));
        Assert.Equal("real input", speechPayload.GetProperty("input").GetString());
        Assert.Equal("flac", speechPayload.GetProperty("response_format").GetString());
        Assert.Equal("reference", speechPayload.GetProperty("ref_audio").GetString());
        Assert.True(speechPayload.GetProperty("stream").GetBoolean());
        Assert.Equal(speechBefore, JsonSerializer.Serialize(speech));

        await using var audio = new MemoryStream([1, 2, 3]);
        var transcription = new OpenAITranscriptionRequest
        {
            Model = "mistral/voxtral-mini-transcribe-3",
            File = new FormFile(audio, 0, audio.Length, "file", "audio.wav") { Headers = new HeaderDictionary(), ContentType = "audio/wav" },
            Language = "nl", Temperature = 0.5f, TimestampGranularities = ["segment"],
            AdditionalProperties = new()
            {
                ["language"] = JsonSerializer.SerializeToElement("wrong"),
                ["diarize"] = JsonSerializer.SerializeToElement(true),
                ["context_bias"] = JsonSerializer.SerializeToElement(new[] { "Northwind" }),
                ["model"] = JsonSerializer.SerializeToElement("wrong"),
                ["stream"] = JsonSerializer.SerializeToElement(false)
            }
        };
        await Collect(provider.OpenAITranscriptionStreamingAsync(transcription));
        Assert.Equal("nl", Assert.Single(transcriptionFields!["language"]));
        Assert.Equal("0.5", Assert.Single(transcriptionFields["temperature"]));
        Assert.Equal("true", Assert.Single(transcriptionFields["diarize"]));
        Assert.Equal("Northwind", Assert.Single(transcriptionFields["context_bias"]));
        Assert.Equal("segment", Assert.Single(transcriptionFields["timestamp_granularities"]));
        Assert.Equal("voxtral-mini-transcribe-3", Assert.Single(transcriptionFields["model"]));
        Assert.Equal("true", Assert.Single(transcriptionFields["stream"]));
    }

    [Theory]
    [InlineData("empty-parts")]
    [InlineData("null-part")]
    [InlineData("null-text")]
    [InlineData("wrong-type")]
    [InlineData("blank-text")]
    [InlineData("blank-model")]
    [InlineData("empty-local-model")]
    [InlineData("invalid-format")]
    [InlineData("blank-format")]
    [InlineData("invalid-rate")]
    [InlineData("invalid-options")]
    public async Task VercelSpeechValidatesInputBeforeNetworkCalls(string invalid)
    {
        var calls = 0;
        var provider = Create(_ => { calls++; return Task.FromResult(Sse("")); });
        var request = SpeechRequest();
        switch (invalid)
        {
            case "empty-parts": request.Text = []; break;
            case "null-part": request.Text = [null!]; break;
            case "null-text": request.Text[0].Text = null!; break;
            case "wrong-type": request.Text[0].Type = "audio"; break;
            case "blank-text": request.Text[0].Text = " \n"; break;
            case "blank-model": request.Model = " "; break;
            case "empty-local-model": request.Model = "mistral/"; break;
            case "invalid-format": request.OutputFormat = new() { Type = "invalid" }; break;
            case "blank-format": request.OutputFormat = new() { Type = "" }; break;
            case "invalid-rate": request.OutputFormat = new() { Type = "wav", Rate = -1 }; break;
            default: request.ProviderOptions = Options("[]"); break;
        }
        var error = await Record.ExceptionAsync(() => Collect(provider.SpeechStreamingAsync(request)));
        Assert.True(error is ArgumentException or NotSupportedException, error?.ToString() ?? "No validation error");
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("bad-base64")]
    [InlineData("blank-audio")]
    [InlineData("blank-model")]
    [InlineData("empty-local-model")]
    [InlineData("null-format")]
    [InlineData("blank-format")]
    [InlineData("raw-pcm")]
    [InlineData("invalid-rate")]
    [InlineData("invalid-options")]
    [InlineData("invalid-array-option")]
    [InlineData("invalid-bool-option")]
    public async Task VercelTranscriptionValidatesInputBeforeNetworkCalls(string invalid)
    {
        var calls = 0;
        var provider = Create(_ => { calls++; return Task.FromResult(Sse("")); });
        var request = TranscriptionRequest();
        switch (invalid)
        {
            case "bad-base64": request.Audio = "invalid!"; break;
            case "blank-audio": request.Audio = " "; break;
            case "blank-model": request.Model = " "; break;
            case "empty-local-model": request.Model = "mistral/"; break;
            case "null-format": request.InputAudioFormat = null!; break;
            case "blank-format": request.InputAudioFormat.Type = ""; break;
            case "raw-pcm": request.InputAudioFormat.Type = "audio/pcm"; break;
            case "invalid-rate": request.InputAudioFormat.Rate = -1; break;
            case "invalid-options": request.ProviderOptions = Options("[]"); break;
            case "invalid-array-option": request.ProviderOptions = Options("""{"context_bias":"invalid"}"""); break;
            default: request.ProviderOptions = Options("""{"diarize":"invalid"}"""); break;
        }
        var error = await Record.ExceptionAsync(() => Collect(provider.TranscriptionStreamingAsync(request)));
        Assert.True(error is ArgumentException or FormatException or NotSupportedException, error?.ToString() ?? "No validation error");
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(Adapter.VercelSpeech)]
    [InlineData(Adapter.OpenAISpeech)]
    [InlineData(Adapter.VercelTranscription)]
    [InlineData(Adapter.OpenAITranscription)]
    public async Task AllAdaptersStopAtFirstDoneAndIgnoreTrailingEvents(Adapter adapter)
    {
        var body = Done(adapter) + Done(adapter) + "event: error\ndata: must not be read\n\n";
        var parts = await Collect(AllParts(Create(_ => Task.FromResult(Sse(body))), adapter));
        Assert.Single(parts, IsFinish);
    }

    [Fact]
    public async Task UnknownEventsAreOnlyExposedWhenRawIsRequestedAndDoNotProduceAudio()
    {
        var provider = Create(_ => Task.FromResult(Sse(Event("future.event", """{"audio_data":"AQ=="}""") + SpeechDone())));
        var request = SpeechRequest();
        request.IncludeRawChunks = true;
        var parts = await Collect(provider.SpeechStreamingAsync(request));
        Assert.Empty(parts.OfType<SpeechAudioPart>());
        Assert.Equal(2, parts.OfType<SpeechRawPart>().Count());
        Assert.Single(parts.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task NullFinalSegmentsRetainOriginalAndStreamedSegmentMetadata()
    {
        var provider = Create(_ => Task.FromResult(Sse(Segment(" timed", 2, 3, "alice") +
            Event("transcription.done", """{"text":" timed","segments":null}"""))));
        var parts = await Collect(provider.TranscriptionStreamingAsync(TranscriptionRequest()));
        var finish = Assert.Single(parts.OfType<TranscriptionFinishPart>());
        Assert.Single(finish.Segments);
        var metadata = finish.ProviderMetadata!["mistral"];
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("segments").ValueKind);
        Assert.Equal("alice", metadata.GetProperty("streamed_segments")[0].GetProperty("speaker_id").GetString());
    }

    private static bool IsSpeech(Adapter adapter) => adapter is Adapter.VercelSpeech or Adapter.OpenAISpeech;
    private static bool IsFinish(object part) => part is SpeechFinishPart or AudioSpeechStreamDone or TranscriptionFinishPart or OpenAITranscriptionTextDone;
    private static string Delta(object part) => part is TranscriptionDeltaPart vercel ? vercel.Delta : ((OpenAITranscriptionTextDelta)part).Delta;
    private static string Event(string name, string json)
        => $"event: {name}\ndata: {json}\n\n";
    private static string SpeechDelta(string audio) => Event("speech.audio.delta", JsonSerializer.Serialize(new { type = "speech.audio.delta", audio_data = audio }));
    private static string SpeechDone() => Event("speech.audio.done", """{"type":"speech.audio.done"}""");
    private static string TextDelta(string text) => Event("transcription.text.delta", JsonSerializer.Serialize(new { type = "transcription.text.delta", text }));
    private static string Segment(string text, double? start, double? end, string? speaker)
        => Event("transcription.segment.delta", JsonSerializer.Serialize(new { type = "transcription.segment.delta", text, start, end, speaker_id = speaker }));
    private static string Done(Adapter adapter) => IsSpeech(adapter) ? SpeechDone() : Event("transcription.done", """{"type":"transcription.done","text":"final"}""");
    private static Dictionary<string, JsonElement> Options(string json) => new() { ["mistral"] = JsonDocument.Parse(json).RootElement.Clone() };
    private static StreamingSpeechRequest SpeechRequest() => new()
    {
        Model = "mistral/voxtral-mini-tts-3-0",
        Text = [new() { Text = "hello" }]
    };
    private static StreamingTranscriptionRequest TranscriptionRequest() => new()
    {
        Model = "mistral/voxtral-mini-transcribe-3",
        Audio = Convert.ToBase64String([1, 2, 3]),
        InputAudioFormat = new() { Type = "wav", Rate = 16000 }
    };

    private static async IAsyncEnumerable<object> AllParts(MistralProvider provider, Adapter adapter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (adapter == Adapter.VercelSpeech)
            await foreach (var part in provider.SpeechStreamingAsync(SpeechRequest(), cancellationToken)) yield return part;
        else if (adapter == Adapter.OpenAISpeech)
            await foreach (var part in provider.OpenAISpeechStreamingAsync(new() { Model = SpeechRequest().Model, Input = "hello" }, cancellationToken)) yield return part;
        else if (adapter == Adapter.VercelTranscription)
            await foreach (var part in provider.TranscriptionStreamingAsync(TranscriptionRequest(), cancellationToken)) yield return part;
        else
        {
            await using var audio = new MemoryStream([1, 2, 3]);
            var request = new OpenAITranscriptionRequest
            {
                Model = TranscriptionRequest().Model,
                File = new FormFile(audio, 0, audio.Length, "file", "audio.wav") { Headers = new HeaderDictionary(), ContentType = "audio/wav" }
            };
            await foreach (var part in provider.OpenAITranscriptionStreamingAsync(request, cancellationToken)) yield return part;
        }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var part in source) result.Add(part);
        return result;
    }

    private static void AssertRequest(HttpRequestMessage message, string path)
    {
        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal("https://api.mistral.ai" + path, message.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", message.Headers.Authorization.Parameter);
        Assert.Contains(message.Headers.Accept, h => h.MediaType == "text/event-stream");
    }

    private static async Task<Dictionary<string, List<string>>> ReadForm(HttpRequestMessage message)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var part in Assert.IsType<MultipartFormDataContent>(message.Content))
        {
            var name = part.Headers.ContentDisposition!.Name!.Trim('"');
            if (!result.TryGetValue(name, out var values)) result[name] = values = [];
            if (name == "file")
            {
                Assert.Equal(new byte[] { 1, 2, 3 }, await part.ReadAsByteArrayAsync());
                Assert.Equal("audio/wav", part.Headers.ContentType!.MediaType);
                Assert.Contains("audio.wav", part.Headers.ContentDisposition.FileName!);
            }
            values.Add(await part.ReadAsStringAsync());
        }
        return result;
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Sse(Stream stream) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } }
    };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static MistralProvider Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }

    private sealed class GatedStream(string prefix, string suffix, bool blockAfterPrefix = true) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private readonly byte[] _suffix = Encoding.UTF8.GetBytes(suffix);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prefixOffset;
        private int _suffixOffset;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released { get; private set; }
        public bool Disposed { get; private set; }
        public int ReadsAfterSuffix { get; private set; }
        public void Release() { Released = true; _release.TrySetResult(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_prefixOffset < _prefix.Length)
                return Copy(_prefix, ref _prefixOffset, buffer);
            if (blockAfterPrefix)
            {
                Waiting.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            if (_suffixOffset < _suffix.Length)
                return Copy(_suffix, ref _suffixOffset, buffer);
            if (blockAfterPrefix)
            {
                ReadsAfterSuffix++;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return 0;
        }
        private static int Copy(byte[] source, ref int offset, Memory<byte> target)
        {
            // Fragment UTF-8 and SSE frames as a network stream can.
            var count = Math.Min(7, Math.Min(target.Length, source.Length - offset));
            source.AsMemory(offset, count).CopyTo(target);
            offset += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
