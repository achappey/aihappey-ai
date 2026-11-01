using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Rime;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Rime;

public sealed class RimeProviderStreamingSpeechTests
{
    [Theory]
    [InlineData("rime/coda", "coda.api.rime.ai", "coda")]
    [InlineData("MISTV3", "mist.api.rime.ai", "mistv3")]
    public async Task Stream_uses_documented_endpoint_default_voice_and_exact_script(string model, string host, string modelId)
    {
        var provider = Create(async (message, cancellationToken) =>
        {
            Assert.Equal(host, message.RequestUri!.Host);
            Assert.Equal("/", message.RequestUri.AbsolutePath);
            Assert.Equal("Bearer test-key", message.Headers.Authorization!.ToString());
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            var body = await Payload(message);
            Assert.Equal(modelId, body.GetProperty("modelId").GetString());
            Assert.Equal("Hello 🌍!", body.GetProperty("text").GetString());
            Assert.Equal("en", body.GetProperty("lang").GetString());
            Assert.Equal(24000, body.GetProperty("samplingRate").GetInt32());
            Assert.False(body.TryGetProperty("speaker", out _));
            Assert.False(body.TryGetProperty("language", out _));
            var response = Audio([1, 2, 3]);
            response.Headers.Add("x-request-id", "rime-request");
            return response;
        });
        var request = StreamRequest(model);
        request.Text = [Part("Hel"), Part("lo "), Part(""), Part("🌍!")];
        var events = await Drain(provider.SpeechStreamingAsync(request));

        Assert.IsType<SpeechStreamStartPart>(events[0]);
        var metadata = Assert.IsType<SpeechResponseMetadataPart>(events[1]);
        Assert.Equal("rime-request", metadata.Headers!["x-request-id"]);
        Assert.NotNull(metadata.Timestamp);
        var audio = Assert.Single(events.OfType<SpeechAudioPart>());
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(audio.Audio));
        Assert.Equal("audio/mpeg", audio.AudioFormat.Type);
        var finish = Assert.IsType<SpeechFinishPart>(events[^1]);
        Assert.Null(finish.Usage);
        Assert.Equal(3, finish.ProviderMetadata!["rime"].GetProperty("bytes").GetInt64());
        Assert.Single(events.OfType<SpeechFinishPart>());
        Assert.Empty(events.OfType<SpeechRawPart>());
    }

    [Theory]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("webm", "audio/webm;codecs=opus")]
    [InlineData("ogg", "audio/ogg;codecs=opus")]
    [InlineData("wav", "audio/wav")]
    [InlineData("pcm", "audio/L16")]
    [InlineData("mulaw", "audio/PCMU")]
    [InlineData("audio/pcm", "audio/L16")]
    [InlineData("audio/webm;codecs=opus", "audio/webm;codecs=opus")]
    [InlineData("audio/ogg;codecs=opus", "audio/ogg;codecs=opus")]
    [InlineData("audio/mp3", "audio/mpeg")]
    public async Task Formats_set_explicit_accept_and_output_rate(string type, string accept)
    {
        var provider = Create(async (message, _) =>
        {
            Assert.Equal(accept.Replace(";", "; "), message.Headers.Accept.ToString());
            Assert.Equal(48000, (await Payload(message)).GetProperty("samplingRate").GetInt32());
            return Audio([1, 2, 3, 4], accept);
        });
        var request = StreamRequest();
        request.OutputFormat = new() { Type = type, Rate = 48000 };
        request.ProviderOptions = Options(new { samplingRate = 16000 });
        var events = await Drain(provider.SpeechStreamingAsync(request));
        var audio = Assert.Single(events.OfType<SpeechAudioPart>());
        Assert.Equal(48000, audio.AudioFormat.Rate);
        Assert.Contains(Warnings(events), warning => warning.GetProperty("feature").GetString() == "providerOptions.rime.samplingRate");
    }

    [Fact]
    public async Task Response_content_type_overrides_requested_format_and_reports_rate()
    {
        var provider = Create((_, _) => Task.FromResult(Audio([1, 2], "audio/wav;rate=16000")));
        var request = StreamRequest();
        request.OutputFormat = new() { Type = "mp3", Rate = 24000 };
        var audio = Assert.Single((await Drain(provider.SpeechStreamingAsync(request))).OfType<SpeechAudioPart>());
        Assert.Equal("audio/wav; rate=16000", audio.AudioFormat.Type);
        Assert.Equal(16000, audio.AudioFormat.Rate);
        var buffered = await provider.SpeechRequest(new SpeechRequest { Model = "coda", Text = "Hello", OutputFormat = "mp3" });
        Assert.Equal("wav", buffered.Audio.Format);
        Assert.Equal("audio/wav; rate=16000", buffered.Audio.MimeType);
    }

    [Fact]
    public async Task Missing_content_type_falls_back_to_accept()
    {
        var provider = Create((_, _) => Task.FromResult(Audio([1, 2], null)));
        var request = StreamRequest();
        request.OutputFormat = new() { Type = "pcm" };
        var audio = Assert.Single((await Drain(provider.SpeechStreamingAsync(request))).OfType<SpeechAudioPart>());
        Assert.Equal("audio/L16", audio.AudioFormat.Type);
        Assert.Equal(24000, audio.AudioFormat.Rate);
    }

    [Fact]
    public async Task Effective_part_options_are_merged_without_mutation_and_voice_model_wins()
    {
        var provider = Create(async (message, _) =>
        {
            var body = await Payload(message);
            Assert.Equal("astra", body.GetProperty("speaker").GetString());
            Assert.Equal("es", body.GetProperty("lang").GetString());
            Assert.Equal(16000, body.GetProperty("samplingRate").GetInt32());
            Assert.Equal(1.25f, body.GetProperty("timeScaleFactor").GetSingle());
            return Audio([1, 2]);
        });
        var request = StreamRequest("rime/coda/astra");
        request.Voice = "request-voice";
        request.Language = "spa";
        request.ProviderOptions = Options(new { language = "fr", samplingRate = 16000, timeScaleFactor = 1.1 });
        var partOptions = Options(new { timeScaleFactor = 1.25, voice = "option-voice" });
        request.Text = [Part("Hel", "part-voice", partOptions), Part("lo", "other-ignored-voice", partOptions)];
        var original = JsonSerializer.Serialize(request);
        var events = await Drain(provider.SpeechStreamingAsync(request));
        Assert.Equal(original, JsonSerializer.Serialize(request));
        Assert.Contains(Warnings(events), warning => warning.GetProperty("feature").GetString() == "voice");
        Assert.Contains(Warnings(events), warning => warning.GetProperty("feature").GetString() == "providerOptions.rime.voice");
    }

    [Fact]
    public async Task Request_voice_is_overridden_by_part_voice_and_identical_effective_settings_are_allowed()
    {
        var provider = Create(async (message, _) =>
        {
            Assert.Equal("cove", (await Payload(message)).GetProperty("speaker").GetString());
            return Audio([1, 2]);
        });
        var request = StreamRequest("mistv3");
        request.Voice = "astra";
        request.Text = [Part("Hel", "cove"), Part("lo", "cove")];
        await Drain(provider.SpeechStreamingAsync(request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changes_in_effective_voice_or_configuration_fail_before_http(bool changeVoice)
    {
        var request = StreamRequest();
        request.Text = changeVoice
            ? [Part("Hel", "astra"), Part("lo", "albion")]
            : [Part("Hel", options: Options(new { timeScaleFactor = 1 })), Part("lo", options: Options(new { timeScaleFactor = 1.5 }))];
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(Create(NoHttp).SpeechStreamingAsync(request)));
    }

    [Fact]
    public async Task Unicode_limit_counts_codepoints_after_concatenation_and_never_truncates()
    {
        var text = string.Concat(Enumerable.Repeat("🌍", 1000));
        var provider = Create(async (message, _) =>
        {
            Assert.Equal(text, (await Payload(message)).GetProperty("text").GetString());
            return Audio([1, 2]);
        });
        var request = StreamRequest();
        // Even a surrogate-pair boundary must not introduce whitespace or change the script.
        request.Text = [Part(text[..1]), Part(text[1..])];
        await Drain(provider.SpeechStreamingAsync(request));
        request.Text = [Part(text), Part("x")];
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(Create(NoHttp).SpeechStreamingAsync(request)));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoHttp).SpeechRequest(new SpeechRequest { Model = "coda", Text = text + "x" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoHttp).OpenAISpeechRequestAsync(new AudioSpeechRequest { Model = "coda", Input = text + "x" }));
    }

    [Fact]
    public async Task Invalid_requests_fail_before_http()
    {
        var provider = Create(NoHttp);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Drain(provider.SpeechStreamingAsync(null!)));
        foreach (var parts in new StreamingSpeechTextPart[][]
        {
            null!, [], [null!], [Part(null!)], [new() { Type = "audio", Text = "hello" }], [Part("")], [Part(" ")]
        })
        {
            var request = StreamRequest();
            request.Text = parts;
            await Assert.ThrowsAsync<ArgumentException>(() => Drain(provider.SpeechStreamingAsync(request)));
        }
        foreach (var model in new[] { "", " ", "coda/", "coda/a/b", "/coda" })
            await Assert.ThrowsAsync<ArgumentException>(() => Drain(provider.SpeechStreamingAsync(StreamRequest(model))));
        foreach (var model in new[] { "mistv2", "arcana", "unknown" })
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(StreamRequest(model))));
            await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest { Model = model, Input = "hello" })));
        }
        foreach (var type in new[] { "flac", "audio/aac", "audio/webm;codecs=vorbis", "audio/L16;rate=8000" })
        {
            var request = StreamRequest();
            request.OutputFormat = new() { Type = type };
            await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(request)));
        }
        foreach (var rate in new[] { 0, -1 })
        {
            var request = StreamRequest();
            request.OutputFormat = new() { Type = "pcm", Rate = rate };
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Drain(provider.SpeechStreamingAsync(request)));
        }
    }

    [Theory]
    [InlineData("coda", "en", true)]
    [InlineData("coda", "ger", true)]
    [InlineData("coda", "ja", false)]
    [InlineData("coda", "nl", false)]
    public async Task Coda_lexicons_are_forwarded_only_for_supported_languages(string model, string language, bool valid)
    {
        var request = StreamRequest(model);
        request.Language = language;
        request.ProviderOptions = Options(new
        {
            customLexicon = new[]
            {
                new
                {
                    spelling = "Aoife",
                    pronunciation = "\" i . f @",
                    future = new { values = new object?[] { 1, null, true } }
                }
            }
        });
        if (!valid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Drain(Create(NoHttp).SpeechStreamingAsync(request)));
            return;
        }
        var provider = Create(async (message, _) =>
        {
            var entry = (await Payload(message)).GetProperty("customLexicon")[0];
            Assert.Equal("Aoife", entry.GetProperty("spelling").GetString());
            Assert.Equal("\" i . f @", entry.GetProperty("pronunciation").GetString());
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(new { values = new object?[] { 1, null, true } }),
                entry.GetProperty("future")));
            return Audio([1, 2]);
        });
        await Drain(provider.SpeechStreamingAsync(request));
    }

    [Fact]
    public async Task Lexicon_size_and_empty_entries_are_validated()
    {
        foreach (var lexicon in new object?[][]
        {
            [null], [new { spelling = "", pronunciation = "x" }], [new { spelling = "x", pronunciation = "" }],
            Enumerable.Repeat<object?>(new { spelling = "x", pronunciation = "x" }, 501).ToArray()
        })
        {
            var request = StreamRequest();
            request.ProviderOptions = Options(new { customLexicon = lexicon });
            await Assert.ThrowsAsync<ArgumentException>(() => Drain(Create(NoHttp).SpeechStreamingAsync(request)));
        }
    }

    [Theory]
    [InlineData("mistv3", "en", true)]
    [InlineData("mistv3", "fr", false)]
    [InlineData("coda", "en", false)]
    public async Task Model_specific_options_and_unsupported_warnings_are_consistent(string model, string language, bool inlinePhonemes)
    {
        var request = StreamRequest(model);
        request.Language = language;
        request.Instructions = "Be cheerful";
        request.Speed = 1.5;
        request.ProviderOptions = Options(new
        {
            pauseBetweenBrackets = true, phonemizeBetweenBrackets = true, inlineSpeedAlpha = "0.5, 3",
            speedAlpha = 2, noTextNormalization = true, saveOovs = false,
            customLexicon = Array.Empty<object>(), timeScaleFactor = 3
        });
        var provider = Create(async (message, cancellationToken) =>
        {
            var body = await Payload(message);
            Assert.Equal(inlinePhonemes, body.TryGetProperty("phonemizeBetweenBrackets", out _));
            Assert.Equal(model == "mistv3", body.TryGetProperty("pauseBetweenBrackets", out _));
            Assert.Equal(model == "mistv3", body.TryGetProperty("inlineSpeedAlpha", out _));
            Assert.Equal(model == "coda", body.TryGetProperty("customLexicon", out _));
            Assert.Equal(3, body.GetProperty("timeScaleFactor").GetSingle());
            Assert.False(body.TryGetProperty("speedAlpha", out _));
            Assert.False(body.TryGetProperty("instructions", out _));
            Assert.False(body.TryGetProperty("noTextNormalization", out _));
            Assert.False(body.TryGetProperty("saveOovs", out _));
            return Audio([1, 2]);
        });
        var warnings = Warnings(await Drain(provider.SpeechStreamingAsync(request))).ToArray();
        Assert.Contains(warnings, warning => warning.GetProperty("feature").GetString() == "speed");
        Assert.Contains(warnings, warning => warning.GetProperty("feature").GetString() == "instructions");
        Assert.Contains(warnings, warning => warning.GetProperty("feature").GetString() == "providerOptions.rime.speedAlpha");
    }

    [Fact]
    public async Task OpenAI_streaming_uses_same_provider_options_as_buffered_speech()
    {
        var payloads = new List<JsonElement>();
        var provider = Create(async (message, _) =>
        {
            payloads.Add(await Payload(message));
            return Audio([1, 2], "audio/L16");
        });
        var rimeOptions = JsonSerializer.Deserialize<JsonElement>("""
            {
              "language": "de",
              "samplingRate": 16000,
              "timeScaleFactor": 1.2,
              "futureObject": {"nested": [{"value": 1}, [true, null, "text"]]},
              "futureArray": [1, {"more": [false, 2.5]}, []],
              "futureString": "unchanged",
              "futureNumber": 12345678901234567890.123456789,
              "futureBoolean": false,
              "futureNull": null
            }
            """);
        var options = new AudioSpeechRequest
        {
            Model = "coda", Input = "Hello", ResponseFormat = "pcm",
            AdditionalProperties = new()
            {
                ["providerOptions"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["rime"] = rimeOptions
                })
            }
        };
        await provider.OpenAISpeechRequestAsync(options);
        var events = await Drain(provider.OpenAISpeechStreamingAsync(options));
        Assert.True(JsonElement.DeepEquals(payloads[0], payloads[1]));
        Assert.Equal("de", payloads[0].GetProperty("lang").GetString());
        Assert.Equal(16000, payloads[0].GetProperty("samplingRate").GetInt32());
        foreach (var name in new[]
                 {
                     "futureObject", "futureArray", "futureString", "futureNumber", "futureBoolean", "futureNull"
                 })
            Assert.True(JsonElement.DeepEquals(rimeOptions.GetProperty(name), payloads[0].GetProperty(name)), name);
        Assert.False(payloads[0].TryGetProperty("providerOptions", out _));
        Assert.False(payloads[0].TryGetProperty("rime", out _));
        Assert.IsType<AudioSpeechStreamDone>(events[^1]);
    }

    [Fact]
    public async Task Unified_conversation_streaming_preserves_rime_provider_options()
    {
        var payloads = new List<JsonElement>();
        var provider = Create(async (message, cancellationToken) =>
        {
            payloads.Add(await Payload(message));
            return Audio([1, 2]);
        });
        var request = new AIRequest
        {
            ProviderId = "rime",
            Model = "rime/coda/astra",
            Metadata = new() { ["rime"] = new { language = "de", samplingRate = 16000, timeScaleFactor = 1.2 } },
            Input = new AIInput
            {
                Items = [new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] }]
            }
        };
        await provider.ExecuteUnifiedAsync(request);
        var events = await Drain(provider.StreamUnifiedAsync(request));
        Assert.DoesNotContain(events, item => item.Event.Type == "tool-output-error");
        Assert.Equal(2, payloads.Count);
        Assert.True(JsonElement.DeepEquals(payloads[0], payloads[1]));
        Assert.Equal("de", payloads[1].GetProperty("lang").GetString());
        Assert.Equal(16000, payloads[1].GetProperty("samplingRate").GetInt32());
    }

    [Fact]
    public async Task Non_audio_success_response_is_rejected_instead_of_emitted_as_audio()
    {
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":\"upstream failure\"}", System.Text.Encoding.UTF8, "application/json")
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(StreamRequest())));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new SpeechRequest { Model = "coda", Text = "Hello" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_at_eof_cannot_produce_a_success_event(bool openAI)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new ChunkStream([[1, 2]]) { AtEof = cancellation.Cancel };
        var provider = Create((_, _) => Task.FromResult(Audio(stream)));
        if (openAI)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest { Model = "coda", Input = "Hello" }, cancellation.Token)));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(provider.SpeechStreamingAsync(StreamRequest(), cancellation.Token)));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Pcm_chunks_keep_whole_samples_and_raw_events_preserve_network_bytes()
    {
        var stream = new ChunkStream([[1], [2, 3, 4], [5], [6]]);
        var provider = Create((_, _) => Task.FromResult(Audio(stream, "audio/L16")));
        var request = StreamRequest();
        request.IncludeRawChunks = true;
        var events = await Drain(provider.SpeechStreamingAsync(request));
        var chunks = events.OfType<SpeechAudioPart>().Select(part => Convert.FromBase64String(part.Audio)).ToArray();
        Assert.All(chunks, chunk => Assert.Equal(0, chunk.Length % 2));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, chunks.SelectMany(chunk => chunk).ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, events.OfType<SpeechRawPart>()
            .SelectMany(part => Convert.FromBase64String(Assert.IsType<string>(part.RawValue))).ToArray());
        Assert.Equal(4, events.OfType<SpeechRawPart>().Count());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Streaming_delivers_audio_before_waiting_for_the_rest_of_the_body()
    {
        var stream = new ChunkStream([[1, 2], [3, 4]]) { GateAfterFirst = true };
        var provider = Create((_, _) => Task.FromResult(Audio(stream)));
        await using var enumerator = provider.SpeechStreamingAsync(StreamRequest()).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync()); // start
        Assert.True(await enumerator.MoveNextAsync()); // response metadata
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<SpeechAudioPart>(enumerator.Current);
        Assert.False(stream.Gate.Task.IsCompleted);
        var next = enumerator.MoveNextAsync().AsTask();
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(next.IsCompleted);
        stream.Gate.TrySetResult();
        Assert.True(await next);
        Assert.IsType<SpeechAudioPart>(enumerator.Current);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<SpeechFinishPart>(enumerator.Current);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_between_chunks_never_reports_success_and_disposes_response(bool openAI)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new ChunkStream([[1, 2], [3, 4]]);
        var provider = Create((_, _) => Task.FromResult(Audio(stream)));
        if (openAI)
        {
            await using var enumerator = provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest { Model = "coda", Input = "Hello" }, cancellation.Token).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        else
        {
            await using var enumerator = provider.SpeechStreamingAsync(StreamRequest(), cancellation.Token).GetAsyncEnumerator();
            for (var i = 0; i < 3; i++) Assert.True(await enumerator.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Cancellation_interrupts_blocked_reads_and_cancellation_before_start_avoids_http()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new ChunkStream([[1, 2], [3, 4]]) { GateAfterFirst = true };
        var provider = Create((_, _) => Task.FromResult(Audio(stream)));
        await using var enumerator = provider.SpeechStreamingAsync(StreamRequest(), cancellation.Token).GetAsyncEnumerator();
        for (var i = 0; i < 3; i++) Assert.True(await enumerator.MoveNextAsync());
        var next = enumerator.MoveNextAsync().AsTask();
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(Create(NoHttp).SpeechStreamingAsync(StreamRequest(), cancellation.Token)));
    }

    [Fact]
    public async Task Early_consumer_disposal_closes_upstream_body()
    {
        var stream = new ChunkStream([[1, 2], [3, 4]]);
        var provider = Create((_, _) => Task.FromResult(Audio(stream)));
        await using (var enumerator = provider.SpeechStreamingAsync(StreamRequest()).GetAsyncEnumerator())
        {
            for (var i = 0; i < 3; i++) Assert.True(await enumerator.MoveNextAsync());
            Assert.False(stream.Disposed);
        }
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_truncated_or_failed_audio_never_emits_completion(bool openAI)
    {
        foreach (var stream in new[]
        {
            new ChunkStream([]), new ChunkStream([[1]]), new ChunkStream([[1, 2]]) { FailAtEof = true }
        })
        {
            var provider = Create((_, _) => Task.FromResult(Audio(stream, "audio/L16")));
            if (openAI)
            {
                var events = new List<IAudioSpeechStreamEvent>();
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await foreach (var item in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest { Model = "coda", Input = "Hello" })) events.Add(item);
                });
                Assert.Empty(events.OfType<AudioSpeechStreamDone>());
            }
            else
            {
                var events = new List<StreamingSpeechPart>();
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await foreach (var item in provider.SpeechStreamingAsync(StreamRequest())) events.Add(item);
                });
                Assert.Empty(events.OfType<SpeechFinishPart>());
            }
            Assert.True(stream.Disposed);
        }
    }

    [Fact]
    public async Task Http_error_contains_status_and_body_and_never_reports_finish()
    {
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("quota exceeded")
        }));
        var events = new List<StreamingSpeechPart>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in provider.SpeechStreamingAsync(StreamRequest())) events.Add(item);
        });
        Assert.Contains("429", error.Message);
        Assert.Contains("quota exceeded", error.Message);
        Assert.Empty(events.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task Catalog_filters_legacy_and_foreign_models_caches_and_remains_public_after_speech()
    {
        var calls = 0;
        var client = new HttpClient(new Handler(async (message, _) =>
        {
            if (message.Method == HttpMethod.Post)
                return Audio([1, 2]);
            calls++;
            Assert.Null(message.Headers.Authorization);
            Assert.Equal("https://users.rime.ai/data/voices/all-v2.json", message.RequestUri!.AbsoluteUri);
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"coda":{"eng":["astra","astra",null,42," "],"nld":["dutch"]},"mistv3":{"eng":["cove"]},"arcana":{"eng":["old"]},"mistv2":{"eng":["old"]},"foreign":{"eng":["no"]}}""")
            };
        }));
        var provider = Create(client);
        await provider.SpeechRequest(new SpeechRequest { Model = "coda", Text = "Hello" });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        var models = (await provider.ListModels(default)).ToArray();
        Assert.Equal(new[] { "rime/coda", "rime/coda/astra", "rime/coda/dutch", "rime/mistv3", "rime/mistv3/cove" }, models.Select(m => m.Id).Order().ToArray());
        Assert.All(models, model => Assert.Equal("speech", model.Type));
        await provider.ListModels(default);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Catalog_does_not_require_key_but_speech_does()
    {
        var provider = Create(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        }))), key: null);
        Assert.Equal(2, (await provider.ListModels(default)).Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest { Model = "coda", Text = "Hello" }));
    }

    private static StreamingSpeechRequest StreamRequest(string model = "coda") => new() { Model = model, Text = [Part("Hello")] };
    private static StreamingSpeechTextPart Part(string text, string? voice = null, Dictionary<string, JsonElement>? options = null)
        => new() { Text = text, Voice = voice, ProviderOptions = options };
    private static Dictionary<string, JsonElement> Options(object value) => new() { ["rime"] = JsonSerializer.SerializeToElement(value) };
    private static IEnumerable<JsonElement> Warnings(List<StreamingSpeechPart> events)
        => Assert.IsType<SpeechStreamStartPart>(events[0]).Warnings.Select(warning => JsonSerializer.SerializeToElement(warning));
    private static async Task<JsonElement> Payload(HttpRequestMessage message)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync());
    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source) result.Add(item);
        return result;
    }
    private static Task<HttpResponseMessage> NoHttp(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("HTTP must not be called.");
    private static HttpResponseMessage Audio(byte[] bytes, string? mimeType = "audio/mpeg")
        => Audio(new MemoryStream(bytes), mimeType);
    private static HttpResponseMessage Audio(Stream stream, string? mimeType = "audio/mpeg")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (mimeType is not null) response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mimeType);
        return response;
    }
    private static RimeProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        => Create(new HttpClient(new Handler(respond)));
    private static RimeProvider Create(HttpClient client, string? key = "test-key")
        => new(new KeyResolver(key), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new ClientFactory(client));
    private sealed class KeyResolver(string? key) : IApiKeyResolver { public string? Resolve(string provider) => key; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class ChunkStream(byte[][] chunks) : Stream
    {
        private int _index;
        public bool Disposed { get; private set; }
        public bool GateAfterFirst { get; init; }
        public bool FailAtEof { get; init; }
        public Action? AtEof { get; init; }
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (GateAfterFirst && _index == 1)
            {
                Waiting.TrySetResult();
                await Gate.Task.WaitAsync(cancellationToken);
            }
            if (_index == chunks.Length)
            {
                AtEof?.Invoke();
                if (FailAtEof) throw new IOException("Simulated upstream read failure");
                return 0;
            }
            var chunk = chunks[_index++];
            chunk.AsMemory().CopyTo(buffer);
            return chunk.Length;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
