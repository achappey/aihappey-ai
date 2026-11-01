using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.NLPCloud;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.NLPCloud;

public sealed class NLPCloudProviderUnifiedTests
{
    private const string NativeResponse = """
        {
          "response":"Hello **world**",
          "paraphrased_text":"Rephrased text",
          "summary_text":"Summary",
          "intent":"Buy a ticket",
          "generated_code":"print('hello')",
          "correction":"Corrected text",
          "keywords_and_keyphrases":["AI","machine learning"],
          "translation_text":"Bonjour",
          "text":"Transcript",
          "url":"https://media.example/speech.wav?signature=original",
          "history":[{"input":"Hi","response":"Hello"}],
          "usage":{"prompt_tokens":10,"completion_tokens":7,"total_tokens":17,
                   "completion_tokens_details":{"reasoning_tokens":2},"cost":0.1234567890123456789,
                   "future":[false,null,{"value":1}]},
          "future":{"nested":[true,null,12345678901234567890.123456789]}
        }
        """;

    public static IEnumerable<object[]> LanguageModels()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Discovery must not call upstream."));
        return provider.NLPCloudLanguageModels.Where(model => model.Type == "language")
            .SelectMany(model => new[] { new object[] { model.Id, false }, new object[] { model.Id, true } });
    }

    [Fact]
    public async Task Discovery_keeps_all_historical_slugs_including_204_translation_targets()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Discovery must not call upstream."));
        var models = (await provider.ListModels()).ToList();
        Assert.Equal(244, models.Count);
        Assert.Equal(244, models.Select(model => model.Id).Distinct().Count());
        Assert.Equal(204, provider.NLLBTranslateModels.Count);
        Assert.Contains(models, model => model.Id == "nlpcloud/t5-base-en-generate-headline/summarization");
        Assert.Contains(models, model => model.Id == "nlpcloud/gpt-oss-120b/kw-kp-extraction");
        Assert.Contains(models, model => model.Id == "nlpcloud/whisper/asr");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_options_envelopes_do_not_leak_other_providers(bool wrapped)
    {
        var options = Parse("""{"future":[null,{"deep":12345678901234567890.123456789}]}""");
        var metadata = new Dictionary<string, object?>
        {
            ["nlpcloud"] = options,
            ["another-provider"] = Parse("""{"secret":"do not forward"}""")
        };
        var provider = CreateProvider(message =>
        {
            var payload = ReadPayload(message);
            Assert.True(JsonElement.DeepEquals(options.GetProperty("future"), payload.GetProperty("future")));
            Assert.False(payload.TryGetProperty("another-provider", out _));
            Assert.False(payload.TryGetProperty("secret", out _));
            Assert.False(payload.TryGetProperty("providerOptions", out _));
            return JsonResponse(NativeResponse);
        });
        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nlpcloud", Model = "gpt-oss-120b", Input = new AIInput { Text = "Task input" },
            Metadata = wrapped ? new() { ["providerOptions"] = metadata } : metadata
        });
    }

    [Theory]
    [MemberData(nameof(LanguageModels))]
    public async Task Every_catalogued_language_slug_keeps_native_endpoint_and_input(string slug, bool stream)
    {
        var model = slug["nlpcloud/".Length..];
        var translation = model.Contains("/translate-to/", StringComparison.Ordinal);
        var endpoint = translation ? "nllb-200-3-3b/translation"
            : model.Contains('/') ? "gpu/" + model : "gpu/" + model + "/chatbot";
        var provider = CreateProvider(message =>
        {
            Assert.Equal("/v1/" + endpoint, message.RequestUri!.AbsolutePath);
            Assert.Equal("Token", message.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", message.Headers.Authorization.Parameter);
            var payload = ReadPayload(message);
            var inputField = !model.Contains('/') ? "input" : model.EndsWith("/code-generation") ? "instruction" : "text";
            Assert.Equal("Task input", payload.GetProperty(inputField).GetString());
            if (translation)
            {
                Assert.Equal(model.Split('/')[2], payload.GetProperty("target").GetString());
                Assert.Equal("", payload.GetProperty("source").GetString());
            }
            if (!model.Contains('/'))
                Assert.Equal(stream, payload.GetProperty("stream").GetBoolean());
            else
                Assert.False(payload.TryGetProperty("stream", out _));
            return JsonResponse(NativeResponse);
        });
        var request = TextRequest(slug);
        if (stream)
        {
            var events = await Collect(provider.StreamUnifiedAsync(request));
            Assert.Equal(new[] { "text-start", "text-delta", "text-end", "finish" }, events.Select(e => e.Event.Type));
            Assert.Equal(slug, Assert.IsType<AIFinishEventData>(events[^1].Event.Data).Model);
            Assert.NotEmpty(Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
        }
        else
        {
            var result = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal(slug, result.Model);
            Assert.NotEmpty(OutputText(result));
        }
    }

    [Fact]
    public async Task Chatbot_maps_instructions_context_and_prior_pairs_without_replaying_latest_turn()
    {
        var provider = CreateProvider(message =>
        {
            var payload = ReadPayload(message);
            Assert.Equal("Task input", payload.GetProperty("input").GetString());
            Assert.Equal("Instruction\nSystem\nDeveloper", payload.GetProperty("context").GetString());
            var history = payload.GetProperty("history");
            Assert.Single(history.EnumerateArray());
            Assert.Equal("Earlier", history[0].GetProperty("input").GetString());
            Assert.Equal("Answer", history[0].GetProperty("response").GetString());
            return JsonResponse(NativeResponse);
        });
        var request = new AIRequest
        {
            ProviderId = "nlpcloud", Model = "gpt-oss-120b", Instructions = "Instruction",
            Input = new AIInput { Items =
            [
                TextItem("system", "System"), TextItem("developer", "Developer"),
                TextItem("user", "Earlier"), TextItem("assistant", "Answer"),
                TextItem("user", "Task input"), TextItem("assistant", "Do not replay")
            ] }
        };
        await provider.ExecuteUnifiedAsync(request);
    }

    [Theory]
    [InlineData("gpt-oss-120b")]
    [InlineData("bart-large-cnn/summarization")]
    [InlineData("nllb-200-3-3b/translate-to/fra_Latn")]
    public async Task Native_options_response_and_usage_preserve_all_JSON_types(string model)
    {
        var options = Parse("""
            {"context":"Native context","history":[{"input":"Custom","response":"History"}],
             "size":"short","source":"eng_Latn","input":"not the user","text":"not the user",
             "target":"not the slug","stream":true,"future":{"nested":[null,false,1]},
             "number":12345678901234567890.123456789,"null":null}
            """);
        var provider = CreateProvider(message =>
        {
            var payload = ReadPayload(message);
            foreach (var key in new[] { "context", "history", "size", "source", "future", "number", "null" })
                Assert.True(JsonElement.DeepEquals(options.GetProperty(key), payload.GetProperty(key)), key);
            if (model == "gpt-oss-120b")
            {
                Assert.Equal("Task input", payload.GetProperty("input").GetString());
                Assert.False(payload.GetProperty("stream").GetBoolean());
            }
            else
                Assert.Equal("Task input", payload.GetProperty("text").GetString());
            if (model.Contains("translate-to"))
                Assert.Equal("fra_Latn", payload.GetProperty("target").GetString());
            return JsonResponse(NativeResponse);
        });
        var response = await provider.ExecuteUnifiedAsync(TextRequest(model, options));
        var raw = ScopedMetadata(response.Metadata).GetProperty("response");
        Assert.True(JsonElement.DeepEquals(Parse(NativeResponse), raw));
        var usage = Assert.IsType<AIUsage>(response.Usage);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(7, usage.OutputTokens);
        Assert.Equal(17, usage.TotalTokens);
        Assert.Equal(2, usage.ReasoningTokens);
        Assert.True(JsonElement.DeepEquals(raw.GetProperty("usage").GetProperty("future"), usage.AdditionalProperties!["future"]));
    }

    [Fact]
    public async Task Gpu_and_multilingual_path_controls_are_not_sent_as_body_options()
    {
        var provider = CreateProvider(message =>
        {
            Assert.Equal("/v1/fra_Latn/bart-large-cnn/summarization", message.RequestUri!.AbsolutePath);
            var payload = ReadPayload(message);
            Assert.False(payload.TryGetProperty("gpu", out _));
            Assert.False(payload.TryGetProperty("lang", out _));
            return JsonResponse(NativeResponse);
        });
        await provider.ExecuteUnifiedAsync(TextRequest("nlpcloud/bart-large-cnn/summarization", Parse("""{"gpu":false,"lang":"fra_Latn"}""")));
    }

    [Fact]
    public async Task Canonical_sampling_controls_are_reported_not_invented_for_task_endpoints()
    {
        var provider = CreateProvider(message =>
        {
            var payload = ReadPayload(message);
            Assert.False(payload.TryGetProperty("temperature", out _));
            Assert.False(payload.TryGetProperty("top_p", out _));
            Assert.False(payload.TryGetProperty("max_length", out _));
            return JsonResponse(NativeResponse);
        });
        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nlpcloud", Model = "gpt-oss-120b", Input = new AIInput { Text = "Task input" },
            Temperature = 0.5, TopP = 0.9, MaxOutputTokens = 50
        });
        Assert.Equal(new[] { "temperature", "top_p", "max_output_tokens" },
            ScopedMetadata(response.Metadata).GetProperty("unsupported_options").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Markdown_code_and_keywords_preserve_native_structured_response()
    {
        var provider = CreateProvider(_ => JsonResponse(NativeResponse));
        var code = await provider.ExecuteUnifiedAsync(TextRequest("gpt-oss-120b/code-generation"));
        Assert.Equal("```\nprint('hello')\n```", OutputText(code));
        var keywords = await provider.ExecuteUnifiedAsync(TextRequest("finetuned-llama-3-70b/kw-kp-extraction"));
        Assert.Equal("- AI\n- machine learning", OutputText(keywords));
        Assert.Equal(2, ScopedMetadata(keywords.Metadata).GetProperty("response").GetProperty("keywords_and_keyphrases").GetArrayLength());
    }

    [Theory]
    [InlineData("hello\n  indented\0 text[D", "ONE]", "hello\n  indented text")]
    [InlineData("data: literal text", "", "data: literal text")]
    [InlineData("before [DO", "not a marker", "before [DOnot a marker")]
    public async Task Raw_stream_preserves_whitespace_and_split_terminators(string first, string second, string expected)
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(first, second))
        });
        var events = await Collect(provider.StreamUnifiedAsync(TextRequest("gpt-oss-120b")));
        Assert.Equal(expected, string.Concat(events.Select(e => e.Event.Data).OfType<AITextDeltaEventData>().Select(e => e.Delta)));
        Assert.Equal(new[] { "text-start", "text-end", "finish" }, events.Where(e => e.Event.Type != "text-delta").Select(e => e.Event.Type));
        Assert.Equal(expected, ScopedMetadata(events[^1].Metadata).GetProperty("response").GetString());
        Assert.Null(Assert.IsType<AIFinishEventData>(events[^1].Event.Data).InputTokens);
    }

    [Fact]
    public async Task Legacy_SSE_stream_preserves_text_and_excludes_framing()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(": keepalive\n\ndata: Hello \0\n\ndata: **world**[DONE]\n\n", Encoding.UTF8, "text/event-stream")
        });
        var events = await Collect(provider.StreamUnifiedAsync(TextRequest("gpt-oss-120b")));
        Assert.Equal("Hello **world**", string.Concat(events.Select(e => e.Event.Data).OfType<AITextDeltaEventData>().Select(e => e.Delta)));
    }

    [Fact]
    public async Task Chat_completion_adapters_preserve_text_model_finish_usage_and_metadata()
    {
        var provider = CreateProvider(_ => JsonResponse(NativeResponse));
        var options = TextRequest("finetuned-llama-3-70b/kw-kp-extraction").ToChatCompletionOptions("nlpcloud");
        var completion = await provider.CompleteChatAsync(options);
        var choice = Assert.Single(completion.Choices);
        Assert.Equal("stop", choice.FinishReason);
        Assert.Contains("machine learning", choice.Message.Content!.ToString());
        Assert.Equal(17, completion.Usage!.TotalTokens);
        Assert.True(JsonElement.DeepEquals(Parse(NativeResponse),
            completion.AdditionalProperties!["provider_metadata"].GetProperty("nlpcloud").GetProperty("response")));

        var updates = await Collect(provider.CompleteChatStreamingAsync(options));
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.Equal(options.Model, update.Model));
        Assert.Equal("assistant", Assert.Single(updates[0].Choices).Delta.Role);
        Assert.Equal("- AI\n- machine learning", Assert.Single(updates[1].Choices).Delta.Content?.ToString());
        Assert.Equal("stop", Assert.Single(updates[^1].Choices).FinishReason);
        Assert.Equal(17, updates[^1].Usage!.TotalTokens);
        Assert.Equal(JsonValueKind.Object, updates[^1].AdditionalProperties!["provider_metadata"].ValueKind);
    }

    [Fact]
    public async Task Responses_and_messages_adapters_preserve_native_metadata_and_stream_lifecycle()
    {
        var provider = CreateProvider(_ => JsonResponse(NativeResponse));
        var unified = new AIRequest
        {
            ProviderId = "nlpcloud", Model = "bart-large-cnn/summarization",
            Input = new AIInput { Text = "Task input" }, MaxOutputTokens = 50
        };
        var response = await provider.ResponsesAsync(unified.ToResponseRequest("nlpcloud"));
        var responseJson = JsonSerializer.SerializeToElement(response);
        Assert.True(JsonElement.DeepEquals(Parse(NativeResponse),
            responseJson.GetProperty("metadata").GetProperty("nlpcloud").GetProperty("response")));
        var message = await provider.MessagesAsync(unified.ToMessagesRequest("nlpcloud"), []);
        Assert.Equal("Summary", Assert.Single(message.Content).Text);
        var parts = await Collect(provider.ResponsesStreamingAsync(unified.ToResponseRequest("nlpcloud")));
        Assert.Contains(parts, part => part is ResponseOutputItemAdded);
        Assert.Contains(parts, part => part is ResponseOutputTextDelta { Delta: "Summary" });
        Assert.Single(parts.OfType<ResponseCompleted>());
        var messageParts = await Collect(provider.MessagesStreamingAsync(unified.ToMessagesRequest("nlpcloud"), []));
        Assert.NotEmpty(messageParts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASR_passes_input_URL_or_inline_audio_natively_and_preserves_segments(bool urlInput)
    {
        const string url = "https://audio.example/file.mp3?signature=keep%2Bexact";
        var provider = CreateProvider(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/v1/gpu/whisper/asr", message.RequestUri!.AbsolutePath);
            var payload = ReadPayload(message);
            Assert.Equal(urlInput ? url : "YXVkaW8=", payload.GetProperty(urlInput ? "url" : "encoded_file").GetString());
            Assert.False(payload.TryGetProperty(urlInput ? "encoded_file" : "url", out _));
            Assert.Equal("en", payload.GetProperty("input_language").GetString());
            return JsonResponse("""{"text":"Transcript","segments":[{"text":"Transcript","start":0,"end":2,"confidence":0.8}],"duration":2}""");
        });
        var request = new AIRequest
        {
            ProviderId = "nlpcloud", Model = "nlpcloud/whisper/asr",
            Input = new AIInput { Items = [new AIInputItem { Role = "user", Content =
                [new AIFileContentPart { Type = "file", MediaType = "audio/mpeg", Data = urlInput ? url : "data:audio/wav;base64,YXVkaW8=" }] }] },
            Metadata = new() { ["nlpcloud"] = Parse("""{"input_language":"en","encoded_file":"override","url":"override"}""") }
        };
        var response = await provider.ExecuteUnifiedAsync(request);
        Assert.Equal("Transcript", OutputText(response));
        Assert.Equal(0.8, ScopedMetadata(response.Metadata).GetProperty("response").GetProperty("segments")[0].GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task Existing_transcription_endpoint_also_sends_URL_without_downloading()
    {
        const string url = "https://audio.example/file.mp3";
        var provider = CreateProvider(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            var payload = ReadPayload(message);
            Assert.Equal(url, payload.GetProperty("url").GetString());
            Assert.False(payload.TryGetProperty("encoded_file", out _));
            return JsonResponse(NativeResponse);
        });
        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper/asr", Audio = url, MediaType = "audio/mpeg"
        });
        Assert.Equal("Transcript", response.Text);
        Assert.True(JsonElement.DeepEquals(Parse(NativeResponse), response.ProviderMetadata!["nlpcloud"]));
    }

    [Fact]
    public async Task Unified_speech_returns_native_audio_URL_without_download()
    {
        var requests = 0;
        var provider = CreateProvider(message =>
        {
            requests++;
            Assert.Equal("/v1/gpu/speech-t5/speech-synthesis", message.RequestUri!.AbsolutePath);
            Assert.Equal("man", ReadPayload(message).GetProperty("voice").GetString());
            return JsonResponse(NativeResponse);
        });
        var response = await provider.ExecuteUnifiedAsync(TextRequest("speech-t5", Parse("""{"voice":"man"}""")));
        var file = Assert.Single(response.Output!.Items!.Single().Content!.OfType<AIFileContentPart>());
        Assert.Equal("https://media.example/speech.wav?signature=original", file.Data);
        var events = await Collect(provider.StreamUnifiedAsync(TextRequest("speech-t5", Parse("""{"voice":"man"}"""))));
        Assert.Equal(2, requests);
        Assert.Single(events, e => e.Event.Type == "file");
        Assert.Equal("finish", events[^1].Event.Type);
    }

    [Fact]
    public async Task Existing_speech_endpoint_keeps_base64_audio_and_forwards_options()
    {
        var provider = CreateProvider(message =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            var payload = ReadPayload(message);
            Assert.Equal("Task input", payload.GetProperty("text").GetString());
            Assert.Equal("man", payload.GetProperty("voice").GetString());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("future").ValueKind);
            return JsonResponse(NativeResponse);
        });
        var result = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "speech-t5", Text = "Task input",
            ProviderOptions = new() { ["nlpcloud"] = Parse("""{"voice":"man","future":null}""") }
        });
        Assert.Equal("AQID", result.Audio.Base64);
        Assert.True(JsonElement.DeepEquals(Parse(NativeResponse), result.ProviderMetadata!["nlpcloud"]));
        Assert.NotNull(result.Request);
    }

    [Fact]
    public async Task Unsupported_tools_formats_and_nontext_input_fail_before_HTTP()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Must not send"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nlpcloud", Model = "gpt-oss-120b", Tools = [new AIToolDefinition { Name = "tool" }]
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nlpcloud", Model = "gpt-oss-120b", ResponseFormat = new { type = "json_object" }
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nlpcloud", Model = "gpt-oss-120b",
            Input = new AIInput { Items = [new AIInputItem { Role = "user", Content =
                [new AIFileContentPart { Type = "file", Data = "https://image.example/img.png", MediaType = "image/png" }] }] }
        }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"summary_text":null}""")]
    [InlineData("""{"summary_text":""}""")]
    [InlineData("""{"error":{"message":"provider error"}}""")]
    [InlineData("[]")]
    public async Task Invalid_success_payloads_do_not_turn_into_empty_success(string body)
    {
        var provider = CreateProvider(_ => JsonResponse(body));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ExecuteUnifiedAsync(TextRequest("bart-large-cnn/summarization")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Collect(provider.StreamUnifiedAsync(TextRequest("bart-large-cnn/summarization"))));
    }

    [Fact]
    public async Task HTTP_errors_preserve_status_and_provider_body_in_both_execution_modes()
    {
        var provider = CreateProvider(_ => JsonResponse("Quota exceeded", HttpStatusCode.TooManyRequests));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(TextRequest("gpt-oss-120b")));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("Quota exceeded", exception.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(provider.StreamUnifiedAsync(TextRequest("gpt-oss-120b"))));
    }

    [Fact]
    public async Task Cancellation_during_stream_read_propagates_without_a_finish()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellingStream(cancellation))
        });
        var events = new List<AIStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var part in provider.StreamUnifiedAsync(TextRequest("gpt-oss-120b"), cancellation.Token))
                events.Add(part);
        });
        Assert.DoesNotContain(events, e => e.Event.Type == "finish");
    }

    [Fact]
    public async Task Buffered_stream_stops_immediately_when_consumer_cancels_after_start()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = CreateProvider(_ => JsonResponse(NativeResponse));
        await using var stream = provider.StreamUnifiedAsync(TextRequest("bart-large-cnn/summarization"), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("text-start", stream.Current.Event.Type);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
    }

    [Fact]
    public async Task Plain_text_response_format_is_a_supported_no_op()
    {
        var provider = CreateProvider(_ => JsonResponse(NativeResponse));
        var result = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nlpcloud", Model = "gpt-oss-120b", Input = new AIInput { Text = "Task input" },
            ResponseFormat = Parse("""{"type":"text"}"""), ToolChoice = Parse("""{"type":"none"}""")
        });
        Assert.Equal("Hello **world**", OutputText(result));
    }

    [Fact]
    public async Task Malformed_metadata_and_missing_translation_target_are_rejected()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Must not send"));
        await Assert.ThrowsAsync<JsonException>(() => provider.ExecuteUnifiedAsync(TextRequest("gpt-oss-120b", Parse("[]"))));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(TextRequest("nllb-200-3-3b/translate-to/")));
    }

    private static AIRequest TextRequest(string model, JsonElement options = default) => new()
    {
        ProviderId = "nlpcloud", Model = model, Id = "test-response", Input = new AIInput { Text = "Task input" },
        Metadata = options.ValueKind == JsonValueKind.Undefined ? null : new() { ["nlpcloud"] = options }
    };

    private static AIInputItem TextItem(string role, string text)
        => new() { Role = role, Content = [new AITextContentPart { Type = "text", Text = text }] };

    private static string OutputText(AIResponse response)
        => string.Join("\n", response.Output!.Items!.SelectMany(item => item.Content!).OfType<AITextContentPart>().Select(part => part.Text));

    private static JsonElement ScopedMetadata(Dictionary<string, object?>? metadata)
        => JsonSerializer.SerializeToElement(metadata!["nlpcloud"], JsonSerializerOptions.Web);

    private static JsonElement ReadPayload(HttpRequestMessage message)
        => Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static NLPCloudProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new KeyResolver(), new ClientFactory(responder));

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var results = new List<T>();
        await foreach (var item in stream)
            results.Add(item);
        return results;
    }

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }

    private sealed class ChunkedStream(params string[] chunks) : MemoryStream
    {
        private int index;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == chunks.Length)
                return ValueTask.FromResult(0);
            var bytes = Encoding.UTF8.GetBytes(chunks[index++]);
            bytes.CopyTo(buffer);
            return ValueTask.FromResult(bytes.Length);
        }
    }

    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }
}
