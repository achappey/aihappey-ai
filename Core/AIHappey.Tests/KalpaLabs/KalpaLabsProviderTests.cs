using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.KalpaLabs;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.KalpaLabs;

public sealed class KalpaLabsProviderTests
{
    private const string ModelId = "kalpa-tts-1";
    private const string Audio = "AQIDBA==";
    private const string Meta = "event: meta\ndata: {\"model\":\"kalpa-tts-1\",\"sample_rate\":24000,\"audio_quality\":\"high\"}\n\n";
    private const string AudioEvent = "event: audio\ndata: {\"pcm_b64\":\"AQIDBA==\",\"frame\":1}\n\n";
    private const string End = "event: end\ndata: {\"request_id\":\"req-1\",\"text\":\"Hello\",\"sample_rate\":24000,\"audio_seconds\":0.5,\"usage\":{\"input_chars\":5,\"input_audio_seconds\":1.2,\"output_audio_seconds\":0.5},\"meta\":{\"frames\":1}}\n\n";

    [Fact]
    public async Task Discovery_joins_live_models_and_voices_without_fabricated_pricing()
    {
        var calls = 0;
        var provider = Create((message, _) =>
        {
            calls++;
            AssertAuth(message);
            Assert.Equal(HttpMethod.Get, message.Method);
            return Task.FromResult(JsonResponse(message.RequestUri!.AbsolutePath == "/v1/models" ? """
                {"data":[
                  {"id":"kalpa-tts-1","display_name":"Kalpa TTS 1","description":"English and Hindi","modes":["tts","converse"],"speakers":["0","1"],"languages":["en","hi"],"default":true},
                  {"id":"future-model","display_name":"Future","description":"New model","modes":["converse"],"speakers":["a","b"],"languages":["fr"],"default":false}
                ]}
                """ : """
                {"data":[
                  {"id":"voice-1","name":"Wren","gender":"feminine","models":["kalpa-tts-1"]},
                  {"id":"voice-2","name":"Grant","gender":"masculine","models":["other-model"]}
                ]}
                """));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(3, models.Length);
        Assert.All(models, model =>
        {
            Assert.Equal("speech", model.Type);
            Assert.Equal("KalpaLabs", model.OwnedBy);
            Assert.Null(model.Pricing);
        });
        var voice = Assert.Single(models, m => m.Id.EndsWith("/voice-1"));
        Assert.Equal("Kalpa TTS 1 / Wren", voice.Name);
        Assert.Contains("hi", voice.Tags!);
        Assert.Contains("feminine", voice.Tags!);
        Assert.Contains(models, m => m.Id == "kalpalabs/future-model");
        await provider.ListModels();
        Assert.Equal(2, calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancelled.Token));
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(24000)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task Named_voice_wav_supports_all_sample_rates_and_generation_parameters(int rate)
    {
        var provider = Create(async (message, _) =>
        {
            AssertAuth(message);
            Assert.Equal("/v1/tts/voice-1", message.RequestUri!.AbsolutePath);
            var body = await Payload(message);
            Assert.Equal(ModelId, body.GetProperty("model").GetString());
            Assert.Equal("Hello नमस्ते", body.GetProperty("text").GetString());
            Assert.Equal("wav", body.GetProperty("output_format").GetString());
            Assert.Equal(rate, body.GetProperty("sample_rate").GetInt32());
            Assert.Equal(1.5, body.GetProperty("params").GetProperty("temperature").GetDouble());
            Assert.Equal(0, body.GetProperty("params").GetProperty("acoustic_temperature").GetDouble());
            Assert.Equal("low", body.GetProperty("params").GetProperty("audio_quality").GetString());
            return WholeResponse();
        });
        var result = await provider.SpeechRequest(new()
        {
            Model = "kalpalabs/kalpa-tts-1/voice-1", Text = "Hello नमस्ते", Voice = "ignored",
            Language = "hi", Speed = 1.1f, Instructions = "ignored", OutputFormat = "audio/wav",
            ProviderOptions = Options(new { sample_rate = rate, @params = new { temperature = 1.5, acoustic_temperature = 0, audio_quality = "low" } })
        });
        Assert.Equal(Audio, result.Audio.Base64);
        Assert.Equal("wav", result.Audio.Format);
        Assert.Equal("audio/wav", result.Audio.MimeType);
        Assert.Equal("req-1", result.ProviderMetadata!["kalpalabs"].GetProperty("request_id").GetString());
        Assert.Equal(4, result.Warnings.Count());
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public async Task Native_conversation_preserves_turns_audio_authoring_and_rendering(string quality)
    {
        var native = new[]
        {
            new { speaker = "reference", text = (string?)"A reference", audio_wav_b64 = (string?)Audio },
            new { speaker = "reply", text = (string?)null, audio_wav_b64 = (string?)null }
        };
        var provider = Create(async (message, _) =>
        {
            Assert.Equal("/v1/converse", message.RequestUri!.AbsolutePath);
            var body = await Payload(message);
            Assert.Equal("reference", body.GetProperty("conversation")[0].GetProperty("speaker").GetString());
            Assert.Equal(Audio, body.GetProperty("conversation")[0].GetProperty("audio_wav_b64").GetString());
            Assert.False(body.GetProperty("conversation")[1].TryGetProperty("text", out var openText));
            Assert.Equal(quality, body.GetProperty("params").GetProperty("audio_quality").GetString());
            Assert.False(body.TryGetProperty("sample_rate", out var sampleRate));
            Assert.False(body.TryGetProperty("output_format", out var format));
            return WholeResponse(converse: true);
        });
        var response = await provider.SpeechRequest(new()
        {
            Model = ModelId, Text = "", ProviderOptions = Options(new { conversation = native, @params = new { audio_quality = quality } })
        });
        Assert.Equal(Audio, response.Audio.Base64);
        Assert.Equal("Hello", response.ProviderMetadata!["kalpalabs"].GetProperty("reply").GetProperty("text").GetString());

        provider = Create(async (message, _) =>
        {
            var open = (await Payload(message)).GetProperty("conversation")[0];
            Assert.Equal("custom", open.GetProperty("speaker").GetString());
            Assert.Equal("Render this", open.GetProperty("text").GetString());
            return WholeResponse(converse: true);
        });
        await provider.SpeechRequest(new() { Model = ModelId, Text = "Render this", ProviderOptions = Options(new { speaker = "custom" }) });
    }

    [Theory]
    [InlineData("scoped")]
    [InlineData("wrapper")]
    [InlineData("native")]
    public async Task OpenAI_buffered_and_streaming_speech_forward_provider_options(string shape)
    {
        var provider = Create(async (message, _) =>
        {
            var body = await Payload(message);
            Assert.Equal(48000, body.GetProperty("sample_rate").GetInt32());
            Assert.Equal(0.5, body.GetProperty("params").GetProperty("temperature").GetDouble());
            return message.RequestUri!.AbsolutePath.EndsWith("/stream")
                ? StreamResponse(new MemoryStream([1, 2, 3, 4]), "audio/pcm")
                : WholeResponse();
        });
        var native = JsonSerializer.SerializeToElement(new { voice = "voice-1", sample_rate = 48000, @params = new { temperature = 0.5 } });
        var additional = shape switch
        {
            "scoped" => new Dictionary<string, JsonElement> { ["kalpalabs"] = native },
            "wrapper" => new() { ["providerOptions"] = JsonSerializer.SerializeToElement(new { kalpalabs = native }) },
            _ => native.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
        };
        var request = new AudioSpeechRequest { Model = ModelId, Input = "Hello", AdditionalProperties = additional };
        var (bytes, mime) = await provider.OpenAISpeechRequestAsync(request);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
        Assert.Equal("audio/wav", mime);
        request.ResponseFormat = "pcm";
        request.StreamFormat = "sse";
        var events = await Drain(provider.OpenAISpeechStreamingAsync(request));
        Assert.Equal(Audio, Assert.Single(events.OfType<AudioSpeechStreamDelta>()).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[^1]);
    }

    [Fact]
    public async Task Sse_speech_forwards_first_audio_before_remainder_or_eof_and_preserves_usage()
    {
        var stream = new PushStream();
        stream.Push(": keepalive\r\n\r\n" + Meta.Replace("\n", "\r\n") + AudioEvent);
        var provider = Create((message, _) =>
        {
            Assert.Equal("/v1/converse/stream", message.RequestUri!.AbsolutePath);
            Assert.Equal("text/event-stream", message.Headers.Accept.ToString());
            return Task.FromResult(StreamResponse(stream, "text/event-stream"));
        });
        var request = Streaming();
        request.IncludeRawChunks = true;
        await using var iterator = provider.SpeechStreamingAsync(request).GetAsyncEnumerator();
        var observed = new List<StreamingSpeechPart>();
        await UntilAudio(iterator, observed);
        Assert.Equal(Audio, Assert.Single(observed.OfType<SpeechAudioPart>()).Audio);
        Assert.Equal(24000, Assert.Single(observed.OfType<SpeechAudioPart>()).AudioFormat.Rate);
        Assert.DoesNotContain(observed, p => p is SpeechFinishPart);
        stream.Push(AudioEvent + End);
        while (await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            observed.Add(iterator.Current);
        var finish = Assert.Single(observed.OfType<SpeechFinishPart>());
        Assert.Equal(5, finish.Usage!.InputCharacters);
        Assert.Equal(1.2, finish.Usage.Raw!["input_audio_seconds"].GetDouble());
        Assert.Equal("req-1", finish.ProviderMetadata!["kalpalabs"].GetProperty("request_id").GetString());
        Assert.Equal(4, observed.OfType<SpeechRawPart>().Count());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Raw_pcm_streaming_aligns_samples_and_never_buffers_the_clip()
    {
        var stream = new PushStream();
        stream.Push([1]);
        stream.Push([2, 3]);
        var provider = Create(async (message, _) =>
        {
            Assert.Equal("/v1/tts/voice-1/stream", message.RequestUri!.AbsolutePath);
            Assert.Equal("pcm_s16le", (await Payload(message)).GetProperty("output_format").GetString());
            return StreamResponse(stream, "audio/pcm");
        });
        await using var iterator = provider.SpeechStreamingAsync(Streaming("kalpalabs/kalpa-tts-1/voice-1")).GetAsyncEnumerator();
        var events = new List<StreamingSpeechPart>();
        await UntilAudio(iterator, events);
        Assert.Equal(new byte[] { 1, 2 }, Convert.FromBase64String(Assert.Single(events.OfType<SpeechAudioPart>()).Audio));
        stream.Push([4]);
        stream.Complete();
        while (await iterator.MoveNextAsync())
            events.Add(iterator.Current);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, events.OfType<SpeechAudioPart>().SelectMany(p => Convert.FromBase64String(p.Audio)).ToArray());
        Assert.Single(events.OfType<SpeechFinishPart>());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Streaming_parts_concatenate_exactly_merge_params_and_do_not_mutate_input()
    {
        var request = Streaming("kalpalabs/kalpa-tts-1/voice-1");
        request.Text =
        [
            new() { Text = "Hel", ProviderOptions = Options(new { @params = new { audio_quality = "low" } }) },
            new() { Text = "lo", ProviderOptions = Options(new { @params = new { audio_quality = "low" } }) }
        ];
        request.OutputFormat = new() { Type = "pcm", Rate = 48000 };
        request.ProviderOptions = Options(new { sample_rate = 16000, @params = new { temperature = 0.25, audio_quality = "high" } });
        var before = JsonSerializer.Serialize(request);
        var provider = Create(async (message, _) =>
        {
            var body = await Payload(message);
            Assert.Equal("Hello", body.GetProperty("text").GetString());
            Assert.Equal(48000, body.GetProperty("sample_rate").GetInt32());
            Assert.Equal(0.25, body.GetProperty("params").GetProperty("temperature").GetDouble());
            Assert.Equal("low", body.GetProperty("params").GetProperty("audio_quality").GetString());
            return StreamResponse(new MemoryStream([1, 2]), "audio/pcm");
        });
        var events = await Drain(provider.SpeechStreamingAsync(request));
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal(48000, Assert.Single(events.OfType<SpeechAudioPart>()).AudioFormat.Rate);
        Assert.Single(Assert.IsType<SpeechStreamStartPart>(events[0]).Warnings);
        request.Text[1].Voice = "other-voice";
        // Model-derived voice wins, so a change in the ignored voice is harmless.
        await Drain(provider.SpeechStreamingAsync(request));
        request.Text[1].ProviderOptions = Options(new { @params = new { temperature = 1.0 } });
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(Create(NoHttp).SpeechStreamingAsync(request)));
    }

    [Theory]
    [InlineData("event: error\ndata: {\"error\":{\"code\":\"overloaded\",\"message\":\"retry\"}}\n\n")]
    [InlineData("event: audio\ndata: {\"pcm_b64\":\"AQIDBA==\"}\n\n")]
    [InlineData(Meta + "event: audio\ndata: {\"pcm_b64\":\"AQ==\"}\n\n")]
    [InlineData(Meta + AudioEvent)]
    [InlineData(Meta + Meta)]
    [InlineData(Meta + "event: unexpected\ndata: {}\n\n")]
    [InlineData(Meta + "event: end\ndata: {\"text\":\"partial\"}")]
    [InlineData(Meta + AudioEvent + "event: error\ndata: {\"code\":\"backend_failed\",\"message\":\"failed mid-generation\"}\n\n")]
    public async Task Sse_protocol_failures_never_emit_success(string sse)
    {
        var stream = new PushStream();
        stream.Push(sse);
        stream.Complete();
        var provider = Create((_, _) => Task.FromResult(StreamResponse(stream, "text/event-stream")));
        var events = new List<StreamingSpeechPart>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var part in provider.SpeechStreamingAsync(Streaming()))
                events.Add(part);
        });
        Assert.DoesNotContain(events, p => p is SpeechFinishPart);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("event: meta\ndata: not-json\n\n")]
    [InlineData(Meta + "event: audio\ndata: {\"pcm_b64\":\"!\"}\n\n")]
    public async Task Empty_or_malformed_sse_is_not_reported_as_success(string sse)
    {
        var provider = Create((_, _) => Task.FromResult(StreamResponse(new MemoryStream(Encoding.UTF8.GetBytes(sse)), "text/event-stream")));
        await Assert.ThrowsAnyAsync<Exception>(() => Drain(provider.OpenAISpeechStreamingAsync(new() { Model = ModelId, Input = "Hello" })));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Http_errors_preserve_status_body_request_id_and_retry_after(int status)
    {
        var provider = Create((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("{\"error\":{\"code\":\"failure\",\"message\":\"upstream detail\"}}")
            };
            response.Headers.Add("X-Request-ID", "failed-request");
            response.Headers.Add("Retry-After", "3");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(new() { Model = ModelId, Text = "Hello" }));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("upstream detail", error.Message);
        Assert.Contains("failed-request", error.Message);
        Assert.Contains("Retry-After=3", error.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => Drain(provider.SpeechStreamingAsync(Streaming())));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
    }

    [Fact]
    public async Task Cancellation_and_consumer_disposal_close_upstream_stream_without_finish()
    {
        foreach (var cancel in new[] { true, false })
        {
            var stream = new PushStream();
            stream.Push(Meta + AudioEvent);
            var provider = Create((_, _) => Task.FromResult(StreamResponse(stream, "text/event-stream")));
            using var cts = new CancellationTokenSource();
            var iterator = provider.SpeechStreamingAsync(Streaming(), cts.Token).GetAsyncEnumerator();
            var events = new List<StreamingSpeechPart>();
            await UntilAudio(iterator, events);
            if (cancel)
            {
                var next = iterator.MoveNextAsync().AsTask();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            }
            await iterator.DisposeAsync();
            Assert.True(stream.Disposed);
            Assert.DoesNotContain(events, p => p is SpeechFinishPart);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(Create(NoHttp).SpeechStreamingAsync(Streaming(), cancelled.Token)));
    }

    [Fact]
    public async Task Cancellation_after_a_raw_event_does_not_forward_its_pending_audio()
    {
        var stream = new PushStream();
        stream.Push(Meta + AudioEvent + End);
        var provider = Create((_, _) => Task.FromResult(StreamResponse(stream, "text/event-stream")));
        var request = Streaming();
        request.IncludeRawChunks = true;
        using var cancelled = new CancellationTokenSource();
        await using var iterator = provider.SpeechStreamingAsync(request, cancelled.Token).GetAsyncEnumerator();
        while (await iterator.MoveNextAsync())
        {
            if (iterator.Current is SpeechRawPart raw &&
                JsonSerializer.SerializeToElement(raw.RawValue).GetProperty("event").GetString() == "audio")
                break;
        }
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Network_failure_after_raw_audio_is_not_mistaken_for_clean_eof()
    {
        var stream = new PushStream();
        stream.Push([1, 2]);
        var provider = Create((_, _) => Task.FromResult(StreamResponse(stream, "audio/pcm")));
        await using var iterator = provider.SpeechStreamingAsync(Streaming(ModelId + "/v")).GetAsyncEnumerator();
        var events = new List<StreamingSpeechPart>();
        await UntilAudio(iterator, events);
        stream.Complete(new IOException("Connection terminated before completion."));
        await Assert.ThrowsAsync<IOException>(() => iterator.MoveNextAsync().AsTask());
        Assert.DoesNotContain(events, p => p is SpeechFinishPart);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Sse_handles_multiline_data_and_preserves_response_headers_and_advertised_rate()
    {
        const string meta = "event: meta\ndata: {\"model\":\"resolved-model\",\ndata: \"sample_rate\":16000,\"audio_quality\":\"medium\"}\n\n";
        var end = End.Replace("\"sample_rate\":24000", "\"sample_rate\":16000");
        var provider = Create((_, _) =>
        {
            var response = StreamResponse(new MemoryStream(Encoding.UTF8.GetBytes(meta + AudioEvent + end)), "text/event-stream");
            response.Headers.Add("X-Request-ID", "header-id");
            return Task.FromResult(response);
        });
        var events = await Drain(provider.SpeechStreamingAsync(Streaming()));
        var metadata = Assert.Single(events.OfType<SpeechResponseMetadataPart>());
        Assert.Equal("header-id", metadata.Headers!["X-Request-ID"]);
        Assert.Equal(16000, Assert.Single(events.OfType<SpeechAudioPart>()).AudioFormat.Rate);
        Assert.Single(events.OfType<SpeechFinishPart>());
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("audio/L16")]
    public async Task Unsupported_encodings_are_rejected_before_http(string format)
    {
        var provider = Create(NoHttp);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new() { Model = ModelId, Text = "Hi", OutputFormat = format }));
        var request = Streaming();
        request.OutputFormat = new() { Type = format };
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(request)));
    }

    [Fact]
    public async Task Invalid_parameters_limits_and_configuration_fail_before_http()
    {
        var provider = Create(NoHttp);
        foreach (var options in new object[]
        {
            new { @params = new { temperature = -0.1 } },
            new { @params = new { acoustic_temperature = 1.6 } },
            new { @params = new { audio_quality = "ultra" } },
            new { sample_rate = 12345 },
            new { voice = "v", conversation = new[] { new { speaker = "0" } } },
            new { conversation = Array.Empty<object>() },
            new { conversation = Enumerable.Range(0, 65).Select(_ => new { speaker = "0" }).ToArray() }
        })
            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                provider.SpeechRequest(new() { Model = ModelId, Text = "Hi", ProviderOptions = Options(options) }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.SpeechRequest(new() { Model = ModelId, Text = "Hi", ProviderOptions = Options(new { sample_rate = 48000 }) }));
        foreach (var text in new[] { "", " ", new string('a', 8001) })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new() { Model = ModelId, Text = text }));
        foreach (var model in new[] { "", "a/", "a/b/c" })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new() { Model = model, Text = "Hi" }));
        var request = Streaming();
        request.OutputFormat = new() { Type = "wav" };
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.SpeechStreamingAsync(request)));
        request.Text = [];
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(provider.SpeechStreamingAsync(request)));
    }

    [Fact]
    public async Task Unicode_text_limit_counts_codepoints_without_truncation()
    {
        var text = string.Concat(Enumerable.Repeat("🌍", 8000));
        var provider = Create(async (message, _) =>
        {
            Assert.Equal(text, (await Payload(message)).GetProperty("text").GetString());
            return WholeResponse();
        });
        await provider.SpeechRequest(new() { Model = ModelId, Voice = "v", Text = text });
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoHttp).SpeechRequest(new() { Model = ModelId, Voice = "v", Text = text + "x" }));
    }

    [Fact]
    public async Task Unified_conversation_maps_history_wav_and_open_turn_and_returns_text_and_audio()
    {
        var provider = Create(async (message, _) =>
        {
            var body = await Payload(message);
            var turns = body.GetProperty("conversation");
            Assert.Equal(3, turns.GetArrayLength());
            Assert.Equal("0", turns[0].GetProperty("speaker").GetString());
            Assert.Equal(Audio, turns[0].GetProperty("audio_wav_b64").GetString());
            Assert.Equal("1", turns[1].GetProperty("speaker").GetString());
            Assert.Equal("reply-speaker", turns[2].GetProperty("speaker").GetString());
            Assert.False(turns[2].TryGetProperty("text", out var openText));
            Assert.Equal(0.5, body.GetProperty("params").GetProperty("temperature").GetDouble());
            return WholeResponse(converse: true);
        });
        var result = await provider.ExecuteUnifiedAsync(new()
        {
            ProviderId = "kalpalabs", Model = ModelId, Temperature = 0.5f,
            Metadata = new() { ["kalpalabs"] = new { speaker = "reply-speaker" } },
            Input = new() { Items =
            [
                new() { Role = "user", Content =
                [
                    new AITextContentPart { Type = "text", Text = "Hello" },
                    new AIFileContentPart { Type = "file", MediaType = "audio/wav", Data = "data:audio/wav;base64," + Audio }
                ] },
                new() { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "Earlier reply" }] }
            ] }
        });
        Assert.Equal("Hello", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        var tool = Assert.Single(result.Output.Items[0].Content!.OfType<AIToolCallContentPart>());
        Assert.True(tool.ProviderExecuted);
        var output = Assert.IsType<CallToolResult>(tool.Output);
        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(Assert.Single(output.Content)).Resource);
        Assert.Equal(Audio, Encoding.UTF8.GetString(blob.Blob.Span));
        Assert.Equal(Audio, JsonSerializer.SerializeToElement(output).GetProperty("content")[0]
            .GetProperty("resource").GetProperty("blob").GetString());
    }

    [Fact]
    public async Task Unified_stream_forwards_audio_resources_before_end_without_accumulating_audio()
    {
        var stream = new PushStream();
        stream.Push(Meta + AudioEvent);
        var provider = Create((_, _) => Task.FromResult(StreamResponse(stream, "text/event-stream")));
        await using var iterator = provider.StreamUnifiedAsync(new() { ProviderId = "kalpalabs", Model = ModelId, Input = new() { Text = "Hi" } }).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<AIToolInputAvailableEventData>(iterator.Current.Event.Data);
        Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var delta = Assert.IsType<AIToolOutputAvailableEventData>(iterator.Current.Event.Data);
        Assert.True(delta.Preliminary);
        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(
            Assert.Single(Assert.IsType<CallToolResult>(delta.Output).Content)).Resource);
        Assert.Equal(Audio, Encoding.UTF8.GetString(blob.Blob.Span));
        stream.Push(End);
        var tail = new List<AIStreamEvent>();
        while (await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            tail.Add(iterator.Current);
        Assert.Equal("Hello", Assert.Single(tail.Select(p => p.Event.Data).OfType<AITextDeltaEventData>()).Delta);
        Assert.Single(tail.Select(p => p.Event.Data).OfType<AIFinishEventData>());
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Conversation_protocol_adapters_preserve_native_provider_options(string protocol)
    {
        var provider = Create(async (message, _) =>
        {
            var body = await Payload(message);
            Assert.Equal("native", body.GetProperty("conversation")[0].GetProperty("speaker").GetString());
            Assert.Equal("Render", body.GetProperty("conversation")[0].GetProperty("text").GetString());
            return WholeResponse(converse: true);
        });
        const string native = """{"kalpalabs":{"conversation":[{"speaker":"native","text":"Render"}]}}""";
        var json = $$"""{"model":"kalpa-tts-1","max_tokens":100,"messages":[{"role":"user","content":"Hi"}],"metadata":{{native}}}""";
        var result = protocol switch
        {
            "chat" => JsonSerializer.SerializeToElement(await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(json)!)),
            "responses" => JsonSerializer.SerializeToElement(await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
                $$"""{"model":"kalpa-tts-1","input":"Hi","metadata":{{native}}}""")!)),
            _ => JsonSerializer.SerializeToElement(await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(json)!, []))
        };
        Assert.Contains("Hello", result.GetRawText());
        Assert.Contains("generate_speech", result.GetRawText());
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("ui")]
    public async Task Conversation_stream_adapters_deliver_each_audio_chunk_before_completion(string protocol)
    {
        var stream = new PushStream();
        stream.Push(Meta + AudioEvent);
        var provider = Create((_, _) => Task.FromResult(StreamResponse(stream, "text/event-stream")));
        const string json = """{"model":"kalpa-tts-1","max_tokens":100,"messages":[{"role":"user","content":"Hi"}]}""";
        var parts = protocol switch
        {
            "chat" => Serialized(provider.CompleteChatStreamingAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(json)!)),
            "responses" => Serialized(provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>(
                """{"model":"kalpa-tts-1","input":"Hi"}""")!)),
            "messages" => Serialized(provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>(json)!, [])),
            _ => Serialized(provider.StreamAsync(JsonSerializer.Deserialize<ChatRequest>(
                """{"id":"chat","model":"kalpa-tts-1","messages":[{"id":"user","role":"user","parts":[{"type":"text","text":"Hi"}]}]}""", JsonSerializerOptions.Web)!))
        };
        await using var iterator = parts.GetAsyncEnumerator();
        var observed = new List<string>();
        while (await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
        {
            observed.Add(iterator.Current);
            if (iterator.Current.Contains("audio/pcm"))
                break;
        }
        Assert.Contains(observed, part => part.Contains("audio/pcm"));
        Assert.False(stream.Disposed);
        stream.Push(AudioEvent + End);
        while (await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            observed.Add(iterator.Current);
        Assert.True(observed.Count(part => part.Contains("audio/pcm") && part.Contains("streaming")) >= 2);
        Assert.Contains(observed, part => part.Contains("Hello"));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Conversation_replays_its_own_speech_tool_output_as_wav_context()
    {
        var calls = 0;
        var provider = Create(async (message, _) =>
        {
            var conversation = (await Payload(message)).GetProperty("conversation");
            if (++calls == 2)
            {
                Assert.Equal(Audio, conversation[0].GetProperty("audio_wav_b64").GetString());
                Assert.Equal("Hello", conversation[0].GetProperty("text").GetString());
                Assert.Equal("Next", conversation[1].GetProperty("text").GetString());
                Assert.Equal(3, conversation.GetArrayLength());
            }
            return WholeResponse(converse: true);
        });
        var first = await provider.ExecuteUnifiedAsync(new() { ProviderId = "kalpalabs", Model = ModelId, Input = new() { Text = "Hi" } });
        await provider.ExecuteUnifiedAsync(new()
        {
            ProviderId = "kalpalabs", Model = ModelId, Input = new() { Items =
            [
                new() { Role = "assistant", Content = first.Output!.Items![0].Content },
                new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Next" }] }
            ] }
        });
    }

    [Fact]
    public async Task Nullable_conversation_audio_preserves_a_valid_text_only_reply()
    {
        var provider = Create((_, _) => Task.FromResult(JsonResponse(
            """{"request_id":"req-1","model":"kalpa-tts-1","reply":{"speaker":"1","text":"Text only","audio":null},"usage":{"input_chars":2,"input_audio_seconds":0,"output_audio_seconds":0},"meta":{}}""")));
        var response = await provider.ExecuteUnifiedAsync(new() { ProviderId = "kalpalabs", Model = ModelId, Input = new() { Text = "Hi" } });
        Assert.Equal("Text only", Assert.IsType<AITextContentPart>(Assert.Single(response.Output!.Items![0].Content!)).Text);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new() { Model = ModelId, Text = "Hi" }));
        Assert.Contains("no audio", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Buffered_pcm_uses_the_native_streaming_transport(bool namedVoice)
    {
        var provider = Create((message, _) =>
        {
            Assert.EndsWith("/stream", message.RequestUri!.AbsolutePath);
            return Task.FromResult(namedVoice
                ? StreamResponse(new MemoryStream([1, 2, 3, 4]), "audio/pcm")
                : StreamResponse(new MemoryStream(Encoding.UTF8.GetBytes(Meta + AudioEvent + End)), "text/event-stream"));
        });
        var response = await provider.SpeechRequest(new() { Model = ModelId, Text = "Hi", Voice = namedVoice ? "voice-1" : null, OutputFormat = "pcm" });
        Assert.Equal(Audio, response.Audio.Base64);
        Assert.Equal("audio/pcm", response.Audio.MimeType);
        Assert.Equal("pcm", response.Audio.Format);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Raw_pcm_empty_and_incomplete_audio_never_finish_successfully(bool empty)
    {
        var provider = Create((_, _) => Task.FromResult(StreamResponse(new MemoryStream(empty ? [] : [1, 2, 3]), "audio/pcm")));
        var events = new List<StreamingSpeechPart>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var part in provider.SpeechStreamingAsync(Streaming(ModelId + "/voice-1")))
                events.Add(part);
        });
        Assert.DoesNotContain(events, p => p is SpeechFinishPart);
    }

    [Fact]
    public void Provider_is_registered_in_the_existing_scope_and_registry()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(new KeyResolver("key"));
        services.AddSingleton<IHttpClientFactory>(new ClientFactory(new HttpClient(new Handler(NoHttp))));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        var provider = registry.GetModelProvider(scope.ServiceProvider, "kalpalabs");
        Assert.IsType<KalpaLabsProvider>(provider);
        Assert.Same(provider, scope.ServiceProvider.GetRequiredService<KalpaLabsProvider>());
    }

    [Fact]
    public async Task Authentication_header_registration_and_unsupported_capabilities_are_explicit()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-KalpaLabs-Key"] = " kalpa-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("kalpa-key", resolver.Resolve("kalpalabs"));
        Assert.Equal("X-KalpaLabs-Key", HeaderApiKeyResolver.SupportedProviderHeaders["kalpalabs"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(NoHttp, key: null).SpeechRequest(new() { Model = ModelId, Text = "Hi" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Create(NoHttp).TranscriptionRequest(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => Create(NoHttp).CreateRealtimeTokenAsync(ModelId, new()));
    }

    private static StreamingSpeechRequest Streaming(string model = ModelId)
        => new() { Model = model, Text = [new() { Text = "Hello" }] };

    private static Dictionary<string, JsonElement> Options(object value)
        => new() { ["kalpalabs"] = JsonSerializer.SerializeToElement(value) };

    private static HttpResponseMessage WholeResponse(bool converse = false)
        => JsonResponse(converse
            ? """{"request_id":"req-1","model":"kalpa-tts-1","reply":{"speaker":"1","text":"Hello","audio":{"format":"wav","sample_rate":24000,"audio_quality":"high","data_b64":"AQIDBA=="}},"usage":{"input_chars":5,"input_audio_seconds":1.2,"output_audio_seconds":0.5},"meta":{}}"""
            : """{"request_id":"req-1","model":"kalpa-tts-1","text":"Hello","audio":{"format":"wav","sample_rate":24000,"audio_quality":"high","data_b64":"AQIDBA=="},"usage":{"input_chars":5,"input_audio_seconds":0,"output_audio_seconds":0.5},"meta":{}}""");

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage StreamResponse(Stream stream, string mime)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new(mime);
        return response;
    }

    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("api.kalpalabs.ai", message.RequestUri!.Host);
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", message.Headers.Authorization.Parameter);
    }

    private static async Task<JsonElement> Payload(HttpRequestMessage message)
        => JsonDocument.Parse(await message.Content!.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream)
            result.Add(item);
        return result;
    }

    private static async IAsyncEnumerable<string> Serialized<T>(IAsyncEnumerable<T> stream)
    {
        await foreach (var part in stream)
            yield return JsonSerializer.Serialize(part);
    }

    private static async Task UntilAudio(IAsyncEnumerator<StreamingSpeechPart> iterator, List<StreamingSpeechPart> observed)
    {
        while (await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
        {
            observed.Add(iterator.Current);
            if (iterator.Current is SpeechAudioPart)
                return;
        }
        Assert.Fail("No audio arrived before the stream ended.");
    }

    private static Task<HttpResponseMessage> NoHttp(HttpRequestMessage message, CancellationToken token)
        => throw new Xunit.Sdk.XunitException("Invalid input must not make an HTTP request.");

    private static KalpaLabsProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, string? key = "test-key")
        => new(new KeyResolver(key), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new ClientFactory(new HttpClient(new Handler(send))));

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class PushStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;
        public bool Disposed { get; private set; }
        public void Push(string data) => Push(Encoding.UTF8.GetBytes(data));
        public void Push(byte[] data) => Assert.True(_channel.Writer.TryWrite(data));
        public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _current.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
                _current = await _channel.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
