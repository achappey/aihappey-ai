using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Interfaze;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Interfaze;

public sealed class InterfazeProviderTests
{
    private const string Audio = "YXVkaW8=";
    private const string Tasks = """
        [{"name":"stt","result":{"text":"Hello 世界","chunks":[{"timestamp":[0.28,4],"text":"Hello 世界"}]}},
         {"name":"stt","result":{"text":"Again","nested":{"confidence":0.99},"values":[1,2,3]}},
         {"name":"translate","result":{"translated_text":"你好","source_language":"en","target_language":"zh"}}]
        """;

    [Fact]
    public async Task Native_stream_preserves_precontext_text_native_tool_indexes_and_extension_fields()
    {
        var choice = new
        {
            index = 2,
            delta = new
            {
                content = "<precontext>" + Tasks + "</precontext>Answer",
                tool_calls = new[] { new { index = 7, id = "client-1", type = "function",
                    function = new { name = "lookup", arguments = "{}" } } },
                custom_delta = new { kept = true }
            },
            finish_reason = "tool_calls",
            custom_choice = "kept"
        };
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: " + JsonSerializer.Serialize(new
            {
                id = "chat-1", model = "interfaze-1", created = 1, choices = new[] { choice },
                precontext = JsonSerializer.Deserialize<JsonElement>(Tasks), custom_root = "kept"
            }) + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        }));

        var update = Assert.Single(await Collect(provider.CompleteChatStreamingAsync(ChatOptions())));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(choice),
            JsonSerializer.SerializeToElement(Assert.Single(update.Choices))));
        Assert.Equal("kept", update.AdditionalProperties!["custom_root"].GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Tasks),
            update.AdditionalProperties["precontext"]));
    }

    [Fact]
    public async Task Unified_completion_strips_inline_precontext_without_changing_native_text()
    {
        var text = "Before<precontext>" + Tasks + "</precontext>After";
        var provider = CreateProvider(_ => Task.FromResult(CompletionResponse(text)));
        var native = await provider.CompleteChatAsync(ChatOptions());
        Assert.Equal(text, JsonSerializer.SerializeToElement(native.Choices.Single())
            .GetProperty("message").GetProperty("content").GetString());

        var response = await provider.ExecuteUnifiedAsync(UnifiedRequest());
        var content = response.Output!.Items!.SelectMany(item => item.Content ?? []).ToList();
        Assert.Equal("BeforeAfter", Assert.Single(content.OfType<AITextContentPart>()).Text);
        Assert.Equal(["stt", "stt", "translate"], content.OfType<AIToolCallContentPart>().Select(tool => tool.ToolName));
        using var expected = JsonDocument.Parse(Tasks);
        var tools = content.OfType<AIToolCallContentPart>().ToList();
        for (var i = 0; i < tools.Count; i++)
        {
            Assert.True(tools[i].ProviderExecuted);
            Assert.Equal("output-available", tools[i].State);
            Assert.True(JsonElement.DeepEquals(expected.RootElement[i].GetProperty("result"),
                Assert.IsType<JsonElement>(tools[i].Output)));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"value\"")]
    [InlineData("42")]
    [InlineData("false")]
    [InlineData("[1,{\"nested\":true}]")]
    public async Task Unified_tools_preserve_all_json_result_types_in_both_modes(string result)
    {
        var task = "{\"name\":\"task\",\"result\":" + result + "}";
        var provider = CreateProvider(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return payload.RootElement.GetProperty("stream").GetBoolean()
                ? SseResponse("<precontext>" + task + "</precontext>OK", 1)
                : JsonResponse(new
                {
                    id = "chat-1", model = "interfaze-1",
                    precontext = JsonSerializer.Deserialize<JsonElement>(task),
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } }
                });
        });
        var response = await provider.ExecuteUnifiedAsync(UnifiedRequest());
        var tool = Assert.Single(response.Output!.Items!.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>());
        var events = await Collect(provider.StreamUnifiedAsync(UnifiedRequest()));
        var output = Assert.IsType<AIToolOutputAvailableEventData>(
            Assert.Single(events, item => item.Event.Type == "tool-output-available").Event.Data);
        using var expected = JsonDocument.Parse(result);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, Assert.IsType<JsonElement>(tool.Output)));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, Assert.IsType<JsonElement>(output.Output)));
    }

    [Fact]
    public async Task Completion_maps_every_precontext_entry_without_losing_native_tools_or_usage()
    {
        var provider = CreateProvider(_ => Task.FromResult(JsonResponse(new
        {
            id = "chat-1", model = "interfaze-1", created = 1,
            precontext = JsonSerializer.Deserialize<JsonElement>(Tasks),
            usage = new { prompt_tokens = 2, completion_tokens = 3, total_tokens = 5 },
            choices = new[] { new
            {
                index = 0, finish_reason = "tool_calls",
                message = new
                {
                    role = "assistant", content = "Answer",
                    tool_calls = new[] { new { id = "client-1", type = "function",
                        function = new { name = "lookup", arguments = "{}" } } }
                }
            } }
        })));

        var completion = await provider.CompleteChatAsync(ChatOptions());
        var native = JsonSerializer.SerializeToElement(completion);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Tasks), native.GetProperty("precontext")));
        var nativeTools = native.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls");
        Assert.Single(nativeTools.EnumerateArray());
        Assert.Equal("client-1", nativeTools[0].GetProperty("id").GetString());
        Assert.False(nativeTools[0].TryGetProperty("provider_executed", out _));

        var response = await provider.ExecuteUnifiedAsync(UnifiedRequest());
        var content = response.Output!.Items!.SelectMany(item => item.Content ?? []).ToList();
        var tools = content.OfType<AIToolCallContentPart>().ToList();

        Assert.Equal("interfaze/interfaze-1", completion.Model);
        Assert.Equal(4, tools.Count);
        Assert.NotEqual(true, tools[^1].ProviderExecuted);
        Assert.Equal(["stt", "stt", "translate"], tools.Take(3).Select(tool => tool.ToolName));
        Assert.Equal(4, tools.Select(tool => tool.ToolCallId).Distinct().Count());
        using var expected = JsonDocument.Parse(Tasks);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(tools[i].ProviderExecuted);
            Assert.Equal("output-available", tools[i].State);
            Assert.True(JsonElement.DeepEquals(expected.RootElement[i].GetProperty("result"),
                Assert.IsType<JsonElement>(tools[i].Output)));
        }
        Assert.Equal("Answer", Assert.Single(content.OfType<AITextContentPart>()).Text);
        Assert.Equal(5, Assert.IsType<AIUsage>(response.Usage).TotalTokens);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(10000)]
    public async Task Stream_strips_split_tags_preserves_results_and_requests_additional_info(int chunkSize)
    {
        var text = "Before<precontext>" + Tasks + "</precontext>After<ordinary>";
        var options = ChatOptions();
        options.Headers = new() { ["x-show-additional-info"] = "false", ["x-test"] = "kept" };
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("true", Assert.Single(request.Headers.GetValues("x-show-additional-info")));
            Assert.Equal("kept", Assert.Single(request.Headers.GetValues("x-test")));
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.True(payload.GetProperty("stream").GetBoolean());
            return SseResponse(text, chunkSize);
        });

        var request = new AIRequest
        {
            ProviderId = "interfaze", Model = "interfaze-1", Input = new AIInput { Text = "Run" },
            Headers = options.Headers
        };
        var events = await Collect(provider.StreamUnifiedAsync(request));
        Assert.Equal("BeforeAfter<ordinary>", string.Concat(events.Select(item => item.Event.Data)
            .OfType<AITextDeltaEventData>().Select(item => item.Delta)));
        var calls = events.Where(item => item.Event.Type == "tool-output-available").ToList();
        Assert.Equal(3, calls.Count);
        using var expected = JsonDocument.Parse(Tasks);
        for (var i = 0; i < calls.Count; i++)
        {
            var output = Assert.IsType<AIToolOutputAvailableEventData>(calls[i].Event.Data);
            Assert.True(output.ProviderExecuted);
            Assert.Equal(expected.RootElement[i].GetProperty("name").GetString(),
                output.ToolName);
            Assert.True(JsonElement.DeepEquals(expected.RootElement[i].GetProperty("result"),
                Assert.IsType<JsonElement>(output.Output)));
        }
        Assert.Equal(3, calls.Select(call => call.Event.Id).Distinct().Count());
        Assert.Single(events, item => item.Event.Type == "finish");
        Assert.Equal(5, Assert.IsType<AIFinishEventData>(events.Single(item => item.Event.Type == "finish").Event.Data).TotalTokens);
        Assert.Equal("false", options.Headers["x-show-additional-info"]);
    }

    [Fact]
    public async Task Unified_stream_emits_completed_provider_tools_before_answer_without_client_execution()
    {
        var provider = CreateProvider(_ => Task.FromResult(SseResponse("<precontext>" + Tasks + "</precontext>Answer", 3)));
        var events = await Collect(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "interfaze", Model = "interfaze-1", Input = new AIInput { Text = "Transcribe" }
        }));

        var inputs = events.Where(item => item.Event.Type == "tool-input-available").ToList();
        var outputs = events.Where(item => item.Event.Type == "tool-output-available").ToList();
        Assert.Equal(3, inputs.Count);
        Assert.Equal(3, outputs.Count);
        using var expected = JsonDocument.Parse(Tasks);
        for (var i = 0; i < 3; i++)
        {
            var input = Assert.IsType<AIToolInputAvailableEventData>(inputs[i].Event.Data);
            var output = Assert.IsType<AIToolOutputAvailableEventData>(outputs[i].Event.Data);
            Assert.True(input.ProviderExecuted);
            Assert.True(output.ProviderExecuted);
            Assert.Equal(inputs[i].Event.Id, outputs[i].Event.Id);
            Assert.Equal(expected.RootElement[i].GetProperty("name").GetString(), output.ToolName);
            Assert.True(JsonElement.DeepEquals(expected.RootElement[i].GetProperty("result"),
                Assert.IsType<JsonElement>(output.Output)));
        }
        Assert.True(events.IndexOf(outputs[^1]) < events.FindIndex(item => item.Event.Type == "text-delta"));
        Assert.Equal("Answer", string.Concat(events.Select(item => item.Event.Data).OfType<AITextDeltaEventData>().Select(item => item.Delta)));
        Assert.Equal(5, Assert.IsType<AIFinishEventData>(Assert.Single(events, item => item.Event.Type == "finish").Event.Data).TotalTokens);
    }

    [Fact]
    public async Task Stream_handles_multiple_blocks_and_closing_tag_inside_json_string()
    {
        const string text = """<precontext>{"name":"scraper","result":{"text":"a </precontext> b"}}</precontext><precontext>{"name":"forecast","result":[1,2]}</precontext>OK""";
        var provider = CreateProvider(_ => Task.FromResult(SseResponse(text, 1)));
        var updates = await Collect(provider.StreamUnifiedAsync(UnifiedRequest()));
        var serialized = JsonSerializer.Serialize(updates);
        Assert.Contains("scraper", serialized);
        Assert.Contains("forecast", serialized);
        var tools = updates.Select(update => update.Event.Data).OfType<AIToolOutputAvailableEventData>().ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("a </precontext> b", Assert.IsType<JsonElement>(tools[0].Output).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Stream_keeps_client_tool_argument_fragments_separate_from_precontext_tools()
    {
        var body = new StringBuilder();
        void Append(object delta, string? finish = null) => body.Append("data: ").Append(JsonSerializer.Serialize(new
        {
            id = "chat-1", model = "interfaze-1", created = 1,
            choices = new[] { new { index = 0, delta, finish_reason = finish } }
        })).Append("\n\n");
        Append(new { content = "<precontext>" + Tasks + "</precontext>" });
        Append(new { tool_calls = new[] { new { index = 0, id = "client-1", type = "function",
            function = new { name = "lookup", arguments = "{\"query\":" } } } });
        Append(new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"test\"}" } } } });
        Append(new { }, "tool_calls");
        body.Append("data: [DONE]\n\n");
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream")
        }));

        var events = await Collect(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "interfaze", Model = "interfaze-1", Input = new AIInput { Text = "Run" }
        }));
        var tools = events.Where(item => item.Event.Type == "tool-input-available").ToList();
        Assert.Equal(4, tools.Count);
        var client = Assert.Single(tools, item => item.Event.Id == "client-1");
        var input = Assert.IsType<AIToolInputAvailableEventData>(client.Event.Data);
        Assert.False(input.ProviderExecuted);
        Assert.Equal("lookup", input.ToolName);
        Assert.Equal("test", Assert.IsType<JsonElement>(input.Input).GetProperty("query").GetString());
        Assert.Equal(3, events.Count(item => item.Event.Type == "tool-output-available"));
    }

    [Theory]
    [InlineData("<precontext>{\"name\":\"stt\",\"result\":{}}")]
    [InlineData("<precontext>{broken}</precontext>")]
    [InlineData("<precontext>{\"name\":\"stt\"}</precontext>")]
    public async Task Invalid_precontext_throws_instead_of_leaking_or_losing_results(string text)
    {
        var provider = CreateProvider(_ => Task.FromResult(SseResponse(text, 2)));
        await Assert.ThrowsAnyAsync<JsonException>(() => Collect(provider.StreamUnifiedAsync(UnifiedRequest())));
        var nonStreaming = CreateProvider(_ => Task.FromResult(CompletionResponse(text)));
        await Assert.ThrowsAnyAsync<JsonException>(() => nonStreaming.ExecuteUnifiedAsync(UnifiedRequest()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_flushes_partial_ordinary_tag_at_finish_or_eof(bool finish)
    {
        var provider = CreateProvider(_ => Task.FromResult(SseResponse("Answer<pre", 2, finish)));
        var updates = await Collect(provider.StreamUnifiedAsync(UnifiedRequest()));
        Assert.Equal("Answer<pre", string.Concat(updates.Select(update => update.Event.Data)
            .OfType<AITextDeltaEventData>().Select(text => text.Delta)));
    }

    [Theory]
    [InlineData(Audio, "data:audio/mpeg;base64," + Audio)]
    [InlineData("data:audio/mpeg;base64," + Audio, "data:audio/mpeg;base64," + Audio)]
    [InlineData("https://example.com/audio.mp3", "https://example.com/audio.mp3")]
    public async Task Chat_audio_is_sent_as_documented_file_part_without_mutating_request(string audio, string expected)
    {
        var options = ChatOptions();
        options.Messages = [new ChatMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(new object[]
            {
                new { type = "text", text = "Transcribe" },
                new { type = "input_audio", input_audio = new { format = "mp3", data = audio } },
                new { type = "file", file = new { filename = "existing.wav", file_data = "https://example.com/existing.wav" } }
            })
        }];
        var before = JsonSerializer.Serialize(options);
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var content = payload.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("Transcribe", content[0].GetProperty("text").GetString());
            Assert.Equal("file", content[1].GetProperty("type").GetString());
            Assert.Equal("audio.mp3", content[1].GetProperty("file").GetProperty("filename").GetString());
            Assert.Equal(expected, content[1].GetProperty("file").GetProperty("file_data").GetString());
            Assert.Equal("existing.wav", content[2].GetProperty("file").GetProperty("filename").GetString());
            return CompletionResponse("Answer");
        });

        await provider.CompleteChatAsync(options);
        Assert.Equal(before, JsonSerializer.Serialize(options));
    }

    [Fact]
    public async Task Unified_audio_uses_provider_local_file_translation_in_both_chat_modes()
    {
        var provider = CreateProvider(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var file = payload.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
            Assert.Equal("file", file.GetProperty("type").GetString());
            Assert.Equal("data:audio/wav;base64," + Audio, file.GetProperty("file").GetProperty("file_data").GetString());
            return payload.RootElement.GetProperty("stream").GetBoolean()
                ? SseResponse("Answer", 100) : CompletionResponse("Answer");
        });
        var request = new AIRequest
        {
            ProviderId = "interfaze", Model = "interfaze-1",
            Input = new AIInput { Items = [new AIInputItem
            {
                Type = "message", Role = "user",
                Content = [new AIFileContentPart { Type = "file", MediaType = "audio/wav", Data = Audio, Filename = "clip.wav" }]
            }] }
        };
        await provider.ExecuteUnifiedAsync(request);
        await Collect(provider.StreamUnifiedAsync(request));
    }

    [Theory]
    [InlineData(Audio)]
    [InlineData("data:audio/wav;base64," + Audio)]
    public async Task Transcription_runs_speech_task_and_preserves_text_chunks_and_raw_metadata(string audio)
    {
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("interfaze-1", payload.RootElement.GetProperty("model").GetString());
            Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
            var messages = payload.RootElement.GetProperty("messages");
            Assert.Equal("<task>speech_to_text</task>", messages[0].GetProperty("content").GetString());
            var file = messages[1].GetProperty("content")[1].GetProperty("file");
            Assert.Equal("data:audio/wav;base64," + Audio, file.GetProperty("file_data").GetString());
            return TranscriptionResponse();
        });

        var result = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "interfaze/interfaze-1", Audio = audio, MediaType = "audio/wav"
        });
        Assert.Equal("Hello world", result.Text);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(0.28f, segment.StartSecond);
        Assert.Equal(4f, segment.EndSecond);
        Assert.Equal("Hello world", segment.Text);
        Assert.Equal("interfaze/interfaze-1", result.Response.ModelId);
        Assert.Equal(5, result.ProviderMetadata!["interfaze"].GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Contains("speech_to_text", result.Request!.Body);
    }

    [Fact]
    public async Task OpenAI_transcription_adapts_upload_options_and_verbose_segments()
    {
        var provider = CreateProvider(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var prompt = payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("spoken language is en", prompt);
            Assert.Contains("Medical vocabulary", prompt);
            Assert.Contains("timestamp for every word", prompt);
            Assert.Equal(0.2f, payload.RootElement.GetProperty("temperature").GetSingle());
            return TranscriptionResponse();
        });
        var result = Assert.IsType<OpenAITranscriptionVerboseResponse>(await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "interfaze-1", File = AudioFile(), ResponseFormat = "verbose_json",
            Language = "en", Prompt = "Medical vocabulary", Temperature = 0.2f, TimestampGranularities = ["word"]
        }));
        Assert.Equal("Hello world", result.Text);
        var segment = Assert.Single(result.Segments!);
        Assert.Equal(0.28, segment.Start, 2);
        Assert.Equal(4, segment.End);
        Assert.Equal("Hello world", Assert.Single(result.Words!).Word);
    }

    [Fact]
    public async Task OpenAI_mimic_stream_sends_one_nonstream_request_and_emits_delta_then_done()
    {
        var count = 0;
        var provider = CreateProvider(async request =>
        {
            count++;
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
            return TranscriptionResponse();
        });
        var events = await Collect(provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
        {
            Model = "interfaze-1", File = AudioFile(), Stream = true
        }));
        Assert.Equal(1, count);
        Assert.Collection(events,
            item => Assert.Equal("Hello world", Assert.IsType<OpenAITranscriptionTextDelta>(item).Delta),
            item => Assert.Equal("Hello world", Assert.IsType<OpenAITranscriptionTextDone>(item).Text));
    }

    [Fact]
    public async Task Vercel_mimic_stream_preserves_raw_response_and_timestamped_segments()
    {
        var provider = CreateProvider(_ => Task.FromResult(TranscriptionResponse()));
        var events = await Collect(provider.TranscriptionStreamingAsync(new StreamingTranscriptionRequest
        {
            Model = "interfaze-1", Audio = Audio, InputAudioFormat = new AudioFormat { Type = "wav" }, IncludeRawChunks = true
        }));
        Assert.IsType<TranscriptionStreamStartPart>(events[0]);
        Assert.IsType<TranscriptionResponseMetadataPart>(events[1]);
        Assert.IsType<TranscriptionRawPart>(events[2]);
        Assert.Equal("Hello world", Assert.IsType<TranscriptionDeltaPart>(events[3]).Delta);
        Assert.Equal("Hello world", Assert.IsType<TranscriptionFinalPart>(events[4]).Text);
        var finish = Assert.IsType<TranscriptionFinishPart>(events[5]);
        Assert.Equal("Hello world", finish.Text);
        Assert.Equal(4, Assert.Single(finish.Segments).EndSecond);
        Assert.NotNull(finish.ProviderMetadata);
    }

    [Fact]
    public async Task Incomplete_timestamps_and_unsupported_options_are_reported_with_raw_results_preserved()
    {
        var provider = CreateProvider(_ => Task.FromResult(CompletionResponse("""
            {"name":"speech_to_text","result":{"text":"Hello","chunks":[{"timestamp":[0,null],"text":"Hello","speaker":"A"}]}}
            """)));
        var request = TranscriptionRequest();
        request.ProviderOptions = new() { ["interfaze"] = JsonSerializer.SerializeToElement(new { custom = true }) };
        var result = await provider.TranscriptionRequest(request);
        Assert.Equal("Hello", result.Text);
        Assert.Empty(result.Segments);
        Assert.Equal(2, result.Warnings.Count());
        Assert.Contains("speaker", result.ProviderMetadata!["interfaze"].GetRawText());
    }

    [Fact]
    public async Task Empty_transcription_is_a_valid_completed_stream_without_a_fake_delta()
    {
        var provider = CreateProvider(_ => Task.FromResult(CompletionResponse("""
            {"name":"speech_to_text","result":{"text":"","chunks":[]}}
            """)));
        var events = await Collect(provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
        {
            Model = "interfaze-1", File = AudioFile()
        }));
        Assert.Equal("", Assert.IsType<OpenAITranscriptionTextDone>(Assert.Single(events)).Text);
    }

    [Fact]
    public async Task Mimic_streams_do_not_emit_success_events_on_http_failure()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"Invalid key"}}""")
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
        {
            Model = "interfaze-1", File = AudioFile()
        })));
        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(provider.TranscriptionStreamingAsync(new StreamingTranscriptionRequest
        {
            Model = "interfaze-1", Audio = Audio, InputAudioFormat = new AudioFormat { Type = "wav" }
        })));
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("")]
    [InlineData("data:audio/wav,unencoded")]
    public async Task Invalid_audio_is_rejected_before_http(string audio)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP must not be called."));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "interfaze-1", Audio = audio, MediaType = "audio/wav"
        }));
    }

    [Fact]
    public async Task Transcription_http_errors_malformed_results_and_cancellation_propagate()
    {
        var failing = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"Bad audio"}}""")
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.TranscriptionRequest(TranscriptionRequest()));
        var malformed = CreateProvider(_ => Task.FromResult(CompletionResponse("""{"name":"speech_to_text","result":{}}""")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => malformed.TranscriptionRequest(TranscriptionRequest()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP must not be called."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider.TranscriptionStreamingAsync(new StreamingTranscriptionRequest
        {
            Model = "interfaze-1", Audio = Audio, InputAudioFormat = new AudioFormat { Type = "wav" }
        }, cts.Token)));
    }

    private static AIRequest UnifiedRequest() => new()
    {
        ProviderId = "interfaze", Model = "interfaze-1", Input = new AIInput { Text = "Run" }
    };
    private static ChatCompletionOptions ChatOptions() => new() { Model = "interfaze-1", Messages = [] };
    private static TranscriptionRequest TranscriptionRequest() => new() { Model = "interfaze-1", Audio = Audio, MediaType = "audio/wav" };
    private static IFormFile AudioFile()
    {
        var bytes = Convert.FromBase64String(Audio);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "clip.wav")
        {
            Headers = new HeaderDictionary(), ContentType = "audio/wav"
        };
    }

    private static HttpResponseMessage TranscriptionResponse() => CompletionResponse("""
        {"name":"speech_to_text","result":{"text":"Hello world","chunks":[{"timestamp":[0.28,4],"text":"Hello world"}]}}
        """);

    private static HttpResponseMessage CompletionResponse(string text) => JsonResponse(new
    {
        id = "chat-1", model = "interfaze-1", created = 1,
        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } } },
        usage = new { prompt_tokens = 2, completion_tokens = 3, total_tokens = 5 }
    });

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage SseResponse(string text, int chunkSize, bool finish = true)
    {
        var body = new StringBuilder();
        for (var i = 0; i < text.Length; i += chunkSize)
            body.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                id = "chat-1", model = "interfaze-1", created = 1,
                choices = new[] { new { index = 0, delta = new { content = text.Substring(i, Math.Min(chunkSize, text.Length - i)) } } }
            })).Append("\n\n");
        if (finish)
        {
            body.Append("data: {\"id\":\"chat-1\",\"model\":\"interfaze-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
            body.Append("data: {\"id\":\"chat-1\",\"model\":\"interfaze-1\",\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n\n");
        }
        body.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static InterfazeProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream)
            items.Add(item);
        return items;
    }
}
