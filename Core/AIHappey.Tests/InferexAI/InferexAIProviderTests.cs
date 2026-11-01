using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.InferexAI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.InferexAI;

public sealed class InferexAIProviderTests
{
    [Fact]
    public async Task Discovery_preserves_virtual_ids_types_and_all_capability_metadata()
    {
        var provider = Provider(r =>
        {
            Assert.Equal("/v1/models", r.RequestUri!.AbsolutePath);
            return Json("""
                {"data":[
                  {"id":"tenant/chat/v2","type":"llm","reasoning":true,"owned_by":"inferexai"},
                  {"id":"stt","type":"stt","streaming":false,"languages":["hi-IN"],"modes":["translit"],"jobs":true},
                  {"id":"tts","type":"tts","voices":["ritu_hi_banking"],"max_input_length":4096,"tuning":{"speed":true}},
                  {"id":"embed","type":"embedding"},
                  {"id":"classify","type":"intent","languages":["mr-IN"]},
                  {"id":"call","type":"voice"}]}
                """);
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(new[] { "language", "transcription", "speech", "embedding", "intent", "voice" }, models.Select(m => m.Type));
        Assert.Equal("inferexai/tenant/chat/v2", models[0].Id);
        Assert.Contains("reasoning", models[0].Tags!);
        Assert.Contains("languages:hi-IN", models[1].Tags!);
        Assert.Contains("inferexai:streaming=false", models[1].Tags!);
        Assert.Contains("inferexai:modes=[\"translit\"]", models[1].Tags!);
        Assert.Contains("voices:ritu_hi_banking", models[2].Tags!);
        Assert.Contains("inferexai:tuning={\"speed\":true}", models[2].Tags!);
    }

    [Fact]
    public async Task Discovery_missing_credentials_skips_http_but_cancellation_is_not_hidden()
    {
        var provider = Provider(_ => throw new Exception("No HTTP expected"), null);
        Assert.Empty(await provider.ListModels());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancelled.Token));
    }

    [Fact]
    public async Task Discovery_rejects_malformed_catalog_instead_of_reporting_an_empty_success()
        => await Assert.ThrowsAsync<KeyNotFoundException>(() => Provider(_ => Json("{}")).ListModels());

    [Fact]
    public async Task Chat_preserves_options_virtual_model_ids_and_usage()
    {
        var provider = AsyncProvider(async r =>
        {
            Assert.Equal("/v1/chat/completions", r.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            Assert.Equal("tenant/chat/v2", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(0.8, body.RootElement.GetProperty("top_p").GetDouble());
            Assert.True(body.RootElement.GetProperty("future_option").GetBoolean());
            return Json("""
                {"id":"chat-1","object":"chat.completion","created":1,"model":"tenant/chat/v2",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
                 "usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6,"future_usage":9},"request_id":"req-chat"}
                """);
        });
        var result = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "inferexai/tenant/chat/v2",
            AdditionalProperties = new() { ["top_p"] = Element(0.8) },
            Metadata = new() { ["inferexai"] = Element(new { future_option = true }).GetRawText() }
        });
        var json = Element(result);
        Assert.Equal(6, json.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal(9, json.GetProperty("usage").GetProperty("future_usage").GetInt32());
        Assert.Equal("req-chat", json.GetProperty("request_id").GetString());
    }

    [Fact]
    public async Task Chat_SSE_preserves_deltas_and_usage()
    {
        var provider = Provider(_ => Sse("""
            data: {"id":"chat-1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"}}]}

            data: {"id":"chat-1","object":"chat.completion.chunk","choices":[],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}

            data: [DONE]
            """));
        var parts = await Collect(provider.CompleteChatStreamingAsync(new ChatCompletionOptions { Model = "chat" }));
        Assert.Equal("Hello", Element(parts[0]).GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(6, Element(parts[1]).GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Chat_HTTP_and_SSE_errors_preserve_the_native_error_details()
    {
        var provider = Provider(_ => new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error":{"code":"model_unavailable","message":"routes failed","request_id":"req-chat"}}""")
        });
        var http = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new() { Model = "chat" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, http.StatusCode);
        Assert.Contains("req-chat", http.Message);
        provider = Provider(_ => Sse("""data: {"error":{"message":"routes failed","code":"model_unavailable","request_id":"req-chat"}}"""));
        var streaming = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(provider.CompleteChatStreamingAsync(new() { Model = "chat" })));
        Assert.Contains("req-chat", streaming.Message);
    }

    [Fact]
    public async Task Embeddings_forward_options_and_map_usage()
    {
        var provider = AsyncProvider(async r =>
        {
            Assert.Equal("/v1/embeddings", r.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            Assert.Equal("tenant/embed", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("future_option").GetBoolean());
            return Json("""{"object":"list","model":"tenant/embed","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2],"extra":true}],"usage":{"prompt_tokens":2,"total_tokens":2,"future_usage":5},"request_id":"req-embed"}""");
        });
        var result = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "inferexai/tenant/embed", Values = ["hello"], ProviderOptions = Options(new { future_option = true })
        });
        Assert.Equal(new[] { 0.1, 0.2 }, result.Embeddings.Single());
        Assert.Equal(2, result.Usage!.Tokens);
        Assert.Equal("req-embed", result.ProviderMetadata!["inferexai"].GetProperty("request_id").GetString());
        Assert.Equal(5, result.ProviderMetadata!["inferexai"].GetProperty("usage").GetProperty("future_usage").GetInt32());
    }

    [Fact]
    public async Task Speech_forwards_native_options_and_never_invents_a_voice()
    {
        var provider = AsyncProvider(async r =>
        {
            using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            Assert.Equal("/v1/audio/speech", r.RequestUri!.AbsolutePath);
            Assert.Equal("tenant/tts", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("persona_hi_style", body.RootElement.GetProperty("voice").GetString());
            Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
            Assert.True(body.RootElement.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            return Audio(new byte[] { 1, 2 }, "audio/mpeg");
        });
        var result = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "inferexai/tenant/tts", Text = "hello", Voice = "persona_hi_style",
            ProviderOptions = Options(new { temperature = 0.7, future_option = new { enabled = true } })
        });
        Assert.Equal("AQI=", result.Audio.Base64);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(new SpeechRequest { Model = "tts", Text = "hello" }));
    }

    [Fact]
    public async Task Speech_SSE_keeps_native_done_fields_usage_and_audio_without_buffering()
    {
        var provider = Provider(_ => Sse("""
            event: speech.audio.delta
            data: {"type":"speech.audio.delta","audio":"AQI=","sequence":9}

            event: speech.audio.done
            data: {"type":"speech.audio.done","format":"pcm","sample_rate":16000,"usage":{"input_tokens":2,"future_usage":7},"request_id":"req-1"}
            """));
        var parts = await Collect(provider.SpeechStreamingAsync(new StreamingSpeechRequest
        {
            Model = "tts", Voice = "persona", Text = [new() { Text = "hel" }, new() { Text = "lo" }],
            OutputFormat = new() { Type = "pcm", Rate = 16000 }, IncludeRawChunks = true
        }));
        Assert.Equal("AQI=", Assert.Single(parts.OfType<SpeechAudioPart>()).Audio);
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Equal(7, finish.Usage!.Raw!["future_usage"].GetInt32());
        Assert.Equal("req-1", finish.ProviderMetadata!["inferexai"].GetProperty("request_id").GetString());
        var native = await Collect(provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "tts", Voice = "persona", Input = "hello", ResponseFormat = "pcm", StreamFormat = "sse"
        }));
        var done = Element(native[^1]);
        Assert.Equal(16000, done.GetProperty("sample_rate").GetInt32());
        Assert.IsAssignableFrom<AudioSpeechStreamDone>(native[^1]);
        Assert.Equal(7, done.GetProperty("providerMetadata").GetProperty("inferexai").GetProperty("usage").GetProperty("future_usage").GetInt32());
    }

    [Fact]
    public async Task Speech_raw_PCM_is_read_as_chunks_and_reports_format_without_inventing_sample_rate()
    {
        var provider = AsyncProvider(async r =>
        {
            using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("stream_format", out _));
            return Audio(new byte[] { 1, 2, 3, 4 }, "audio/L16");
        });
        var parts = await Collect(provider.SpeechStreamingAsync(new StreamingSpeechRequest
        {
            Model = "tts", Voice = "persona", Text = [new() { Text = "hello" }],
            OutputFormat = new() { Type = "pcm" }, ProviderOptions = Options(new { stream_format = (string?)null })
        }));
        var audio = Assert.Single(parts.OfType<SpeechAudioPart>());
        Assert.Null(audio.AudioFormat.Rate);
        Assert.Equal("AQIDBA==", audio.Audio);
        Assert.Single(parts.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task Raw_mulaw_always_reports_the_documented_8000_Hz_output()
    {
        var parts = await Collect(Provider(_ => Audio(new byte[] { 1 }, "audio/basic")).SpeechStreamingAsync(new StreamingSpeechRequest
        {
            Model = "tts", Voice = "persona", Text = [new() { Text = "hello" }],
            OutputFormat = new() { Type = "mulaw", Rate = 16000 },
            ProviderOptions = Options(new { stream_format = (string?)null })
        }));
        Assert.Equal(8000, Assert.Single(parts.OfType<SpeechAudioPart>()).AudioFormat.Rate);
    }

    [Theory]
    [InlineData("data: {\"type\":\"speech.audio.delta\",\"audio\":\"AQI=\"}")]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"message\":\"failed\",\"code\":\"model_unavailable\",\"request_id\":\"req-err\"}}")]
    public async Task Speech_does_not_emit_finish_on_truncated_or_failed_SSE(string events)
    {
        var seen = new List<StreamingSpeechPart>();
        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var part in Provider(_ => Sse(events)).SpeechStreamingAsync(SpeechStream()))
                seen.Add(part);
        });
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(seen.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task Speech_rejects_conflicting_part_configuration_and_truncated_raw_PCM()
    {
        var request = SpeechStream();
        request.Text = [new() { Text = "hello", Voice = "one" }, new() { Text = "there", Voice = "two" }];
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(Provider(_ => throw new Exception("No HTTP")).SpeechStreamingAsync(request)));
        request = SpeechStream();
        request.ProviderOptions = Options(new { stream_format = (string?)null });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(Provider(_ => Audio(new byte[] { 1 }, "audio/L16")).SpeechStreamingAsync(request)));
    }

    [Fact]
    public async Task Transcription_maps_language_code_and_preserves_words_segments_usage_and_unknown_fields()
    {
        var provider = AsyncProvider(async r =>
        {
            Assert.Equal("/v1/audio/transcriptions", r.RequestUri!.AbsolutePath);
            var form = Assert.IsType<MultipartFormDataContent>(r.Content);
            var fields = await Fields(form);
            Assert.Equal("tenant/stt", fields["model"]);
            Assert.Equal("hi-IN", fields["language_code"]);
            Assert.Equal("translit", fields["mode"]);
            Assert.Equal("true", fields["future_option"]);
            Assert.Equal(new byte[] { 1, 2 }, await form.Single(f => f.Headers.ContentDisposition!.Name!.Trim('"') == "file").ReadAsByteArrayAsync());
            return Json("""{"text":"hello","language":"hi","duration":2.5,"segments":[{"start":0.1,"end":2,"text":"hello","speaker":"SPEAKER_00"}],"words":[{"word":"hello","start":0.1,"end":2}],"usage":{"type":"duration","seconds":2.5},"request_id":"req-1"}""");
        });
        var result = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "inferexai/tenant/stt", Audio = "data:audio/wav;base64,AQI=", MediaType = "audio/wav",
            ProviderOptions = Options(new { language = "hi-IN", mode = "translit", future_option = true })
        });
        Assert.Equal(2.5, result.DurationInSeconds);
        Assert.Equal(0.1, Assert.Single(result.Segments).StartSecond);
        var raw = result.ProviderMetadata!["inferexai"];
        Assert.Equal("SPEAKER_00", raw.GetProperty("segments")[0].GetProperty("speaker").GetString());
        Assert.Equal("req-1", raw.GetProperty("request_id").GetString());
        Assert.Equal(2.5, raw.GetProperty("usage").GetProperty("seconds").GetDouble());
    }

    [Fact]
    public async Task Async_jobs_submit_poll_and_preserve_diarized_result()
    {
        var calls = 0;
        var provider = AsyncProvider(async r =>
        {
            if (++calls == 1)
            {
                Assert.Equal("/v1/audio/transcriptions/jobs", r.RequestUri!.AbsolutePath);
                var fields = await Fields(Assert.IsType<MultipartFormDataContent>(r.Content));
                Assert.Equal("true", fields["diarize"]);
                Assert.Equal("2", fields["num_speakers"]);
                Assert.DoesNotContain("async_job", fields.Keys);
                Assert.DoesNotContain("poll_interval_seconds", fields.Keys);
                return Json("""{"id":"txjob_1","status":"queued"}""");
            }
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("/v1/audio/transcriptions/jobs/txjob_1", r.RequestUri!.AbsolutePath);
            return calls == 2 ? Json("""{"id":"txjob_1","status":"processing"}""")
                : Json("""{"id":"txjob_1","status":"completed","duration":40,"text":"hello","segments":[{"speaker":"SPEAKER_01","start":1,"end":2,"text":"hello"}]}""");
        });
        var result = await provider.TranscriptionRequest(Transcription(new { async_job = true, diarize = true, num_speakers = 2, poll_interval_seconds = 0.001 }));
        Assert.Equal(3, calls);
        Assert.Equal("SPEAKER_01", result.ProviderMetadata!["inferexai"].GetProperty("result").GetProperty("segments")[0].GetProperty("speaker").GetString());
    }

    [Fact]
    public async Task Async_jobs_failure_and_cancellation_never_return_a_success_transcript()
    {
        var calls = 0;
        var provider = Provider(_ => ++calls == 1 ? Json("""{"id":"job","status":"queued"}""")
            : Json("""{"id":"job","status":"failed","error":{"message":"bad audio","request_id":"req-err"}}"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscriptionRequest(Transcription(new { async_job = true, poll_interval_seconds = 0.001 })));
        Assert.Contains("req-err", error.Message);
        using var cancellation = new CancellationTokenSource();
        provider = Provider(_ =>
        {
            cancellation.Cancel();
            return Json("""{"id":"job","status":"queued"}""");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TranscriptionRequest(Transcription(new { async_job = true }), cancellation.Token));
    }

    [Fact]
    public async Task OpenAI_transcription_preserves_extension_options_and_diarized_speakers()
    {
        var provider = AsyncProvider(async r =>
        {
            var fields = await Fields(Assert.IsType<MultipartFormDataContent>(r.Content));
            Assert.Equal("hi-IN", fields["language_code"]);
            Assert.Equal("true", fields["diarize"]);
            Assert.Equal("transcribe", fields["mode"]);
            return Json("""{"id":"job","status":"completed","text":"hello","duration":40,"segments":[{"speaker":"SPEAKER_01","start":1,"end":2,"text":"hello"}]}""");
        });
        var result = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "stt", Language = "hi-IN", ResponseFormat = "diarized_json",
            File = new FormFile(new MemoryStream(new byte[] { 1, 2 }), 0, 2, "file", "audio.wav")
            {
                Headers = new HeaderDictionary(), ContentType = "audio/wav"
            },
            AdditionalProperties = new() { ["mode"] = Element("transcribe") }
        });
        Assert.Equal("SPEAKER_01", Assert.Single(Assert.IsType<OpenAITranscriptionDiarizedResponse>(result).Segments).Speaker);
    }

    [Fact]
    public async Task Unified_transcription_mapping_forwards_native_options_for_both_request_modes()
    {
        var provider = AsyncProvider(async r =>
        {
            var fields = await Fields(Assert.IsType<MultipartFormDataContent>(r.Content));
            Assert.Equal("hi-IN", fields["language_code"]);
            Assert.Equal("translit", fields["mode"]);
            return Json("""{"text":"hello"}""");
        });
        var request = new AIRequest
        {
            ProviderId = "inferexai", Model = "inferexai/stt",
            Input = new()
            {
                Items = [new AIInputItem
                {
                    Role = "user", Content = [new AIFileContentPart
                    {
                        Type = "file", MediaType = "audio/wav", Data = "AQI="
                    }]
                }]
            }
        };
        var fields = new Dictionary<string, JsonElement> { ["language_code"] = Element("hi-IN"), ["mode"] = Element("translit") };
        var response = await provider.ExecuteUnifiedTranscriptionAsync(request, additionalProperties: fields);
        var text = Assert.IsType<AITextContentPart>(Assert.Single(Assert.Single(response.Output!.Items!).Content!));
        Assert.Equal("hello", text.Text);
        var stream = await Collect(provider.StreamUnifiedTranscriptionAsync(request, additionalProperties: fields));
        Assert.Equal("hello", Assert.Single(stream.Select(s => s.Event.Data).OfType<AITextDeltaEventData>()).Delta);
    }

    [Fact]
    public async Task HTTP_errors_preserve_status_plain_body_and_error_envelope()
    {
        var provider = Provider(_ => new(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("""{"error":{"message":"empty wallet","code":"insufficient_credit","request_id":"req-err"}}""")
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.TranscriptionRequest(Transcription()));
        Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
        Assert.Contains("req-err", error.Message);
        provider = Provider(_ => new(HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("edge upload rejected") });
        error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.TranscriptionRequest(Transcription()));
        Assert.Contains("edge upload rejected", error.Message);
    }

    [Fact]
    public async Task WebSocket_maps_authoritative_finals_and_preserves_raw_events()
    {
        var socket = new FakeSocket([
            """{"type":"transcription_session.created","session":{"model":"stt","sample_rate":16000},"request_id":"req-ws"}""",
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"hell"}""",
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"one","transcript":"hello","start":0.5,"end":2.3}""",
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"one","transcript":"hello","start":0.5,"end":2.3}""",
            """{"type":"transcription_session.done","usage":{"seconds":2.3,"future":true}}"""
        ]);
        var provider = RealtimeProvider(socket);
        var parts = await Collect(provider.TranscriptionStreamingAsync(Realtime()));
        Assert.Equal("hell", Assert.Single(parts.OfType<TranscriptionDeltaPart>()).Delta);
        Assert.Single(parts.OfType<TranscriptionFinalPart>());
        var finish = Assert.Single(parts.OfType<TranscriptionFinishPart>());
        Assert.Equal("hello", finish.Text);
        Assert.Equal(0.5, Assert.Single(finish.Segments).StartSecond);
        Assert.True(finish.Usage!["future"].GetBoolean());
        Assert.Equal(5, finish.ProviderMetadata!["inferexai"].GetProperty("events").GetArrayLength());
        Assert.Equal(new byte[] { 1, 2 }, socket.Sent.Single(s => s.Kind == WebSocketMessageType.Binary).Data);
        Assert.Contains("input_audio_buffer.commit", Encoding.UTF8.GetString(socket.Sent.Single(s => s.Kind == WebSocketMessageType.Text).Data));
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"error\":{\"code\":\"model_unavailable\",\"request_id\":\"req-ws\"}}")]
    [InlineData("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"transcript\":\"hello\"}")]
    public async Task WebSocket_failure_or_early_close_has_no_finish(string message)
    {
        var seen = new List<StreamingTranscriptionPart>();
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var part in RealtimeProvider(new FakeSocket([message])).TranscriptionStreamingAsync(Realtime())) seen.Add(part);
        });
        Assert.IsType<InvalidOperationException>(error);
        Assert.Empty(seen.OfType<TranscriptionFinishPart>());
    }

    [Fact]
    public async Task WebSocket_requires_advertised_streaming_and_raw_audio()
    {
        var provider = Provider(_ => Json("""{"data":[{"id":"stt","type":"stt","streaming":false}]}"""));
        provider.TranscriptionSocketFactory = (_, _, _) => throw new Exception("Must not connect.");
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(provider.TranscriptionStreamingAsync(Realtime())));
        var request = Realtime();
        request.InputAudioFormat.Type = "audio/wav";
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(RealtimeProvider(new FakeSocket([])).TranscriptionStreamingAsync(request)));
    }

    [Fact]
    public async Task WebSocket_cancellation_and_failed_upload_do_not_report_finish()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(RealtimeProvider(new FakeSocket([])).TranscriptionStreamingAsync(Realtime(), cancelled.Token)));
        var socket = new FakeSocket([]) { SendError = new IOException("upload failed") };
        var error = await Assert.ThrowsAsync<IOException>(() =>
            Collect(RealtimeProvider(socket).TranscriptionStreamingAsync(Realtime())));
        Assert.Equal("upload failed", error.Message);
    }

    private static InferexAIProvider RealtimeProvider(FakeSocket socket)
    {
        var provider = Provider(_ => Json("""{"data":[{"id":"stt","type":"stt","streaming":true}]}"""));
        provider.TranscriptionSocketFactory = (uri, key, _) =>
        {
            Assert.Equal("test-key", key);
            Assert.Contains("model=stt", uri.Query);
            Assert.Contains("encoding=pcm_s16le", uri.Query);
            Assert.DoesNotContain("api_key", uri.Query);
            return Task.FromResult<WebSocket>(socket);
        };
        return provider;
    }

    private static StreamingTranscriptionRequest Realtime() => new()
    {
        Model = "inferexai/stt", Audio = "AQI=", InputAudioFormat = new() { Type = "pcm_s16le", Rate = 16000 }, IncludeRawChunks = true
    };
    private static StreamingSpeechRequest SpeechStream() => new()
    {
        Model = "tts", Text = [new() { Text = "hello" }], Voice = "persona", OutputFormat = new() { Type = "pcm" }
    };
    private static TranscriptionRequest Transcription(object? options = null) => new()
    {
        Model = "stt", Audio = "AQI=", MediaType = "audio/wav", ProviderOptions = options is null ? null : Options(options)
    };
    private static Dictionary<string, JsonElement> Options(object value) => new() { ["inferexai"] = Element(value) };
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, value.GetType());
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value + "\n\n", Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Audio(byte[] bytes, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }
    private static async Task<Dictionary<string, string>> Fields(MultipartFormDataContent form)
    {
        var result = new Dictionary<string, string>();
        foreach (var field in form.Where(f => f.Headers.ContentDisposition!.FileName is null))
            result[field.Headers.ContentDisposition!.Name!.Trim('"')] = await field.ReadAsStringAsync();
        return result;
    }
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var part in source) result.Add(part);
        return result;
    }
    private static InferexAIProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> responder, string? key = "test-key")
        => AsyncProvider(r => Task.FromResult(responder(r)), key);
    private static InferexAIProvider AsyncProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Keys(key), new Factory(new Handler(async r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal(key, r.Headers.Authorization?.Parameter);
            return await responder(r);
        })), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    private sealed class Keys(string? key) : IApiKeyResolver { public string? Resolve(string provider) { Assert.Equal("inferexai", provider); return key; } }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("inferexai", name);
            return new(new InferexAIErrorHandler { InnerHandler = handler }, false);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }
    private sealed class FakeSocket(IEnumerable<string> messages) : WebSocket
    {
        private readonly Queue<byte[]> _messages = new(messages.Select(Encoding.UTF8.GetBytes));
        public List<(WebSocketMessageType Kind, byte[] Data)> Sent { get; } = [];
        public Exception? SendError { get; init; }
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => "";
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SendError is not null) return Task.FromException(SendError);
            Sent.Add((messageType, buffer.ToArray()));
            return Task.CompletedTask;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_messages.TryDequeue(out var message)) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            message.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true));
        }
    }
}
