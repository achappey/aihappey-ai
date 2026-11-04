using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Creora;
using AIHappey.HeaderAuth;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Creora;

public sealed class CreoraProviderTests
{
    private const string ImageResult = """
        {"created":1790571234,"data":[{"b64_json":"AQID","revised_prompt":"A blue sea"}],
         "usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5},"credits":16,"Future":{"Enabled":true}}
        """;
    private const string ChatResult = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1790571234,"model":"creora-chat",
         "choices":[{"index":0,"message":{"role":"assistant","content":"# Answer\n\n![sea](https://cdn.example/sea.webp)"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10,"credits":0.1},"native":{"kept":null}}
        """;
    private const string RawOptions = """
        {"FutureField":{"KeepCASE":[true,2.5,null,{"url":"https://input.example/a.png?sig=a%2Bb&x=1"}]},
         "explicitNull":null,"flag":false,"number":3.25,"text":"0042","array":[1,"two",false,null]}
        """;

    [Fact]
    public void Registration_is_scoped_and_header_key_is_resolved()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        var registration = Assert.Single(services,
            item => !item.IsKeyedService && item.ImplementationType == typeof(CreoraProvider));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Contains(services, item => item.IsKeyedService && item.ServiceType == typeof(IModelProvider)
            && Equals(item.ServiceKey, "creora") && item.Lifetime == ServiceLifetime.Scoped);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Creora-Key"] = "header-test-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("header-test-key", resolver.Resolve("creora"));
    }

    [Fact]
    public async Task Discovery_authenticates_caches_and_keeps_native_ids_and_capabilities()
    {
        var calls = 0;
        var provider = Create(request =>
        {
            calls++;
            Assert.Equal("/api/v1/models", request.RequestUri!.AbsolutePath);
            return Json("""
                {"object":"list","data":[
                 {"id":"creora-chat","object":"model","created":1790571234,"owned_by":"creora","type":"chat","context_window":1234,"max_tokens":100},
                 {"id":"creora-network","owned_by":"network","type":"language","tags":["streaming","text"]},
                 {"id":"creora-image","type":"image"},{"id":"creora-video","type":"video"},
                 {"id":"org/Future.Model","name":"Future model","type":"new-modality","tags":["NewCapability"]}]}
                """);
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(5, models.Length);
        Assert.Equal("creora/creora-chat", models[0].Id);
        Assert.Equal("language", models[0].Type);
        Assert.Equal(1234, models[0].ContextWindow);
        Assert.Equal(100, models[0].MaxTokens);
        Assert.Equal("network", models[1].OwnedBy);
        Assert.Equal(["streaming", "text"], models[1].Tags);
        Assert.Equal("image", models[2].Type);
        Assert.Equal("video", models[3].Type);
        Assert.Equal("creora/org/Future.Model", models[4].Id);
        Assert.Equal("new-modality", models[4].Type);
        Assert.Equal(["NewCapability"], models[4].Tags);
        await provider.ListModels();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Missing_key_does_not_send_requests()
    {
        var provider = Create(_ => throw new InvalidOperationException("Unexpected HTTP"), null);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new() { Model = "creora-chat" }));
    }

    [Theory]
    [InlineData("creora-chat")]
    [InlineData("creora-network")]
    public async Task Chat_preserves_native_options_urls_markdown_usage_and_unknown_response_fields(string model)
    {
        var options = ChatOptions(model);
        var provider = Create(request =>
        {
            Assert.Equal("/api/v1/chat/completions", request.RequestUri!.AbsolutePath);
            var payload = Body(request);
            Assert.Equal(model, payload.GetProperty("model").GetString());
            Assert.False(payload.GetProperty("stream").GetBoolean());
            AssertRawOptions(payload);
            Assert.Equal(0.2, payload.GetProperty("temperature").GetDouble());
            Assert.Equal(0.8, payload.GetProperty("top_p").GetDouble());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("seed").ValueKind);
            Assert.False(payload.TryGetProperty("providerMetadata", out _));
            Assert.False(payload.TryGetProperty("unused-provider", out _));
            Assert.Equal("https://input.example/a.png?sig=a%2Bb&x=1",
                payload.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal(7, payload.GetProperty("messages")[0].GetProperty("FutureMessageField").GetInt32());
            return Json(ChatResult);
        });
        var response = await provider.CompleteChatAsync(options);
        var json = JsonSerializer.SerializeToElement(response);
        Assert.Equal("# Answer\n\n![sea](https://cdn.example/sea.webp)",
            json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(10, json.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal(0.1, json.GetProperty("usage").GetProperty("credits").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("native").GetProperty("kept").ValueKind);
        AssertRawOptions(JsonSerializer.SerializeToElement(options.AdditionalProperties));
    }

    [Fact]
    public async Task Chat_unset_tools_do_not_erase_raw_tools_and_explicit_standard_tools_win()
    {
        var options = ChatOptions("creora-chat");
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RawOptions)!;
        raw["tools"] = Element("""[{"type":"future_tool","Custom":null}]""");
        options.AdditionalProperties = raw;
        var calls = 0;
        var provider = Create(request =>
        {
            var tool = Body(request).GetProperty("tools")[0];
            Assert.Equal(++calls == 1 ? "future_tool" : "function", tool.GetProperty("type").GetString());
            return Json(ChatResult);
        });
        await provider.CompleteChatAsync(options);
        options.Tools = JsonSerializer.Deserialize<ChatCompletionToolDefinition[]>(
            """[{"type":"function","function":{"name":"weather","parameters":{"type":"object"}}}]""")!;
        await provider.CompleteChatAsync(options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_chat_does_not_materialize_or_overwrite_native_options_with_mapping_defaults(bool streaming)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RawOptions)!;
        raw["store"] = Element("true");
        raw["stream_options"] = Element("null");
        raw["tool_choice"] = Element("""{"FutureSelector":null}""");
        raw["tools"] = Element("""[{"type":"future_tool","Custom":null}]""");
        var calls = 0;
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"data":[{"id":"creora-chat","type":"chat"}]}""");
            calls++;
            var payload = Body(request);
            foreach (var property in raw)
                Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Key)), property.Key);
            Assert.Equal(0.2, payload.GetProperty("temperature").GetDouble());
            Assert.Equal(0.3, payload.GetProperty("top_p").GetDouble());
            Assert.Equal(50, payload.GetProperty("max_completion_tokens").GetInt32());
            return streaming
                ? Sse("data: {\"id\":\"chatcmpl-1\",\"model\":\"creora-chat\",\"created\":1,\"choices\":[{\"index\":0,\"delta\":{\"content\":\"# Answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")
                : Json(ChatResult);
        });
        var input = new AIRequest
        {
            ProviderId = "creora", Model = "creora-chat", Temperature = 0.2, TopP = 0.3, MaxOutputTokens = 50,
            Input = new() { Items = [new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "A sea" }] }] },
            Metadata = new() { ["creora"] = JsonSerializer.SerializeToElement(raw) }
        };
        if (streaming)
        {
            var events = new List<AIStreamEvent>();
            await foreach (var item in provider.StreamUnifiedAsync(input)) events.Add(item);
            Assert.Contains(events, item => item.Event.Type == "text-delta");
            Assert.Contains(events, item => item.Event.Type == "finish");
        }
        else
            Assert.Equal("completed", (await provider.ExecuteUnifiedAsync(input)).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Chat_stream_is_native_sse_with_keepalives_markdown_usage_and_raw_options()
    {
        var provider = Create(request =>
        {
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            AssertRawOptions(Body(request));
            Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            return Sse("""
                : keep alive

                data: {"id":"chatcmpl-1","model":"creora-network","created":1790571234,"choices":[{"index":0,"delta":{"role":"assistant","content":"# Heading\n"},"finish_reason":null}],"Future":null}

                : keep alive

                data: {"id":"chatcmpl-1","model":"creora-network","created":1790571234,"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5,"credits":0.1}}

                data: [DONE]

                """);
        });
        var chunks = new List<ChatCompletionUpdate>();
        await foreach (var chunk in provider.CompleteChatStreamingAsync(ChatOptions("creora-network")))
            chunks.Add(chunk);
        Assert.Equal(2, chunks.Count);
        var first = JsonSerializer.SerializeToElement(chunks[0]);
        Assert.Equal("creora/creora-network", chunks[0].Model);
        Assert.Equal("# Heading\n", first.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("Future").ValueKind);
        Assert.Equal(5, JsonSerializer.SerializeToElement(chunks[1]).GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Stream_errors_and_cancellation_are_not_reported_as_success()
    {
        var provider = Create(_ => Sse("data: {\"error\":{\"code\":\"insufficient_credits\",\"message\":\"Add credits\"}}\n\n"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions("creora-chat"))) { }
        });
        Assert.Contains("insufficient_credits", error.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions("creora-chat"), cancellation.Token)) { }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_preserve_raw_options_and_only_override_set_standard_values(bool explicitValues)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RawOptions)!;
        raw["size"] = Element("\"1024x1024\"");
        raw["n"] = Element("1");
        raw["model"] = Element("\"must-not-win\"");
        raw["prompt"] = Element("\"must-not-win\"");
        var provider = Create(request =>
        {
            Assert.Equal("/api/v1/images/generations", request.RequestUri!.AbsolutePath);
            var payload = Body(request);
            AssertRawOptions(payload);
            Assert.Equal("creora-image", payload.GetProperty("model").GetString());
            Assert.Equal("A sea", payload.GetProperty("prompt").GetString());
            Assert.Equal(explicitValues ? 2 : 1, payload.GetProperty("n").GetInt32());
            Assert.Equal(explicitValues ? "2048x2048" : "1024x1024", payload.GetProperty("size").GetString());
            return Json(ImageResult);
        });
        var result = await provider.ImageRequest(new()
        {
            Model = "creora-image", Prompt = "A sea",
            N = explicitValues ? 2 : null, Size = explicitValues ? "2048x2048" : null,
            ProviderOptions = new() { ["creora"] = JsonSerializer.SerializeToElement(raw) }
        });
        Assert.Equal("data:image/webp;base64,AQID", Assert.Single(result.Images!));
        Assert.Equal(5, result.Usage!.TotalTokens);
        Assert.Equal(16, result.ProviderMetadata!["creora"].GetProperty("credits").GetInt32());
        Assert.Equal("A blue sea", result.ProviderMetadata["creora"].GetProperty("data")[0].GetProperty("revised_prompt").GetString());
        Assert.Equal("request-123", result.Response.Headers!["x-request-id"]);
    }

    [Fact]
    public async Task OpenAI_images_keep_unknown_fields_nulls_and_explicit_standard_precedence()
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RawOptions)!;
        raw["n"] = Element("9");
        raw["size"] = Element("\"1024x1024\"");
        var provider = Create(request =>
        {
            var payload = Body(request);
            AssertRawOptions(payload);
            Assert.Equal(1, payload.GetProperty("n").GetInt32());
            Assert.Equal("1024x1024", payload.GetProperty("size").GetString());
            return Json(ImageResult);
        });
        var result = await provider.OpenAIImageGenerationRequestAsync(new()
        {
            Model = "creora-image", Prompt = "A sea", N = 1, AdditionalProperties = raw
        });
        Assert.Equal("webp", result.OutputFormat);
        Assert.Equal("A blue sea", Assert.Single(result.Data!).RevisedPrompt);
        Assert.Equal(5, result.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Image_stream_emits_only_completed_output()
    {
        var provider = Create(_ => Json(ImageResult));
        var events = new List<IOpenAIImageStreamEvent>();
        await foreach (var item in provider.OpenAIImageGenerationStreamingAsync(new() { Model = "creora-image", Prompt = "A sea" }))
            events.Add(item);
        var completed = Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.Equal("AQID", completed.B64Json);
        Assert.Equal("webp", completed.OutputFormat);
        Assert.Equal(5, completed.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Unified_image_stream_keeps_raw_options_metadata_usage_and_tool_lifecycle()
    {
        var options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RawOptions)!;
        options["n"] = Element("\"0042\"");
        options["size"] = Element("null");
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"data":[{"id":"creora-image","type":"image"}]}""");
            AssertRawOptions(Body(request));
            Assert.Equal("0042", Body(request).GetProperty("n").GetString());
            Assert.Equal(JsonValueKind.Null, Body(request).GetProperty("size").ValueKind);
            return Json(ImageResult);
        });
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(new()
        {
            ProviderId = "creora", Model = "creora-image",
            Input = new() { Items = [new() { Type = "message", Role = "user", Content = [new AITextContentPart { Type = "text", Text = "A sea" }] }] },
            Metadata = new() { ["creora"] = JsonSerializer.SerializeToElement(options) }
        }))
            events.Add(item);
        Assert.Equal(["tool-input-available", "tool-output-available", "finish"], events.Select(item => item.Event.Type));
        Assert.IsType<AIToolOutputAvailableEventData>(events[1].Event.Data);
        Assert.Equal(5, Assert.IsType<AIFinishEventData>(events[2].Event.Data).TotalTokens);
    }

    [Fact]
    public async Task Video_start_is_async_and_passes_options_urls_and_native_nulls_without_downloads()
    {
        var calls = 0;
        var provider = Create(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/videos", request.RequestUri!.AbsolutePath);
            var payload = Body(request);
            AssertRawOptions(payload);
            Assert.Equal("creora-video", payload.GetProperty("model").GetString());
            Assert.Equal("A sea", payload.GetProperty("prompt").GetString());
            return Json("""{"id":"video_abc","object":"video","model":"creora-video","status":"queued","progress":0,"credits":256}""");
        });
        var result = await provider.StartVideoOperation(new()
        {
            Model = "creora-video", Prompt = "A sea", ProviderOptions = Options()
        });
        Assert.Equal(1, calls);
        Assert.Equal("video_abc", result.Operation);
        Assert.Equal(256, result.ProviderMetadata!["creora"].GetProperty("credits").GetInt32());
        Assert.Equal("creora/creora-video", result.Response.ModelId);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public async Task Video_poll_keeps_progress_without_fetching_content(string status)
    {
        var provider = Create(request =>
        {
            Assert.Equal("/api/v1/videos/video_abc", request.RequestUri!.AbsolutePath);
            return Json($$"""{"id":"video_abc","model":"creora-video","status":"{{status}}","progress":42}""");
        });
        var result = Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus("video_abc"));
        Assert.Equal(42, result.ProviderMetadata!["creora"].GetProperty("progress").GetInt32());
    }

    [Fact]
    public async Task Completed_video_downloads_authenticated_native_mp4_and_keeps_metadata()
    {
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent([1, 2, 3]);
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return new(HttpStatusCode.OK) { Content = content };
            }
            return Json("""{"id":"video_abc","model":"creora-video","status":"completed","progress":100,"completed_at":1790571250,"credits":256}""");
        });
        var result = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus("video_abc"));
        var video = Assert.Single(result.Videos);
        Assert.Equal("AQID", video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(256, result.ProviderMetadata!["creora"].GetProperty("credits").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_failed_video_content_is_not_reported_as_completed(bool failed)
    {
        var provider = Create(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
                return Json("""{"id":"video_abc","model":"creora-video","status":"completed"}""");
            return failed ? Json("""{"error":{"code":"content_not_ready"}}""", HttpStatusCode.Conflict)
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.GetVideoOperationStatus("video_abc"));
        Assert.Contains(failed ? "content_not_ready" : "empty video content", error.Message);
    }

    [Theory]
    [InlineData("""{"created":1,"data":[]}""")]
    [InlineData("""{"created":1,"data":[{"b64_json":null}]}""")]
    public async Task Empty_image_results_are_explicit_errors(string body)
    {
        var provider = Create(_ => Json(body));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ImageRequest(new() { Model = "creora-image", Prompt = "A sea" }));
        Assert.Contains("no image data", error.Message);
    }

    [Fact]
    public async Task Failed_video_keeps_native_error_and_unknown_states_throw()
    {
        var provider = Create(_ => Json("""
            {"id":"video_abc","model":"creora-video","status":"failed","error":{"code":"generation_failed","message":"Try again"},"refunded":true}
            """));
        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("video_abc"));
        Assert.Contains("generation_failed", result.Error);
        Assert.True(result.ProviderMetadata!["creora"].GetProperty("refunded").GetBoolean());
        provider = Create(_ => Json("""{"id":"video_abc","status":"new-state"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetVideoOperationStatus("video_abc"));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("chat-stream")]
    [InlineData("models")]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("video-poll")]
    public async Task Native_http_errors_preserve_status_and_body_without_retries(string capability)
    {
        var calls = 0;
        var provider = Create(_ =>
        {
            calls++;
            return Json("""{"error":{"code":"insufficient_credits","message":"Add credits","param":null}}""",
                HttpStatusCode.PaymentRequired);
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            switch (capability)
            {
                case "chat": await provider.CompleteChatAsync(ChatOptions("creora-chat")); break;
                case "chat-stream":
                    await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions("creora-chat"))) { }
                    break;
                case "models": await provider.ListModels(); break;
                case "image": await provider.ImageRequest(new() { Model = "creora-image", Prompt = "A sea" }); break;
                case "video": await provider.StartVideoOperation(new() { Model = "creora-video", Prompt = "A sea" }); break;
                default: await provider.GetVideoOperationStatus("video_abc"); break;
            }
        });
        Assert.Contains("insufficient_credits", error.Message);
        Assert.Contains("Add credits", error.Message);
        Assert.Equal(HttpStatusCode.PaymentRequired, Assert.IsType<HttpRequestException>(error).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unverified_editing_is_explicitly_unsupported_and_never_downloads_input()
    {
        var provider = Create(_ => throw new InvalidOperationException("Unexpected HTTP"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new()
        {
            Model = "creora-image", Prompt = "A sea", Files = [new ImageFileUrl { Url = "https://input.example/image.png" }]
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.StartVideoOperation(new()
        {
            Model = "creora-video", Prompt = "A sea", Image = new VideoFileUrl { Url = "https://input.example/image.png" }
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(new()));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("video-poll")]
    public async Task Media_cancellation_prevents_http_requests(string capability)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Create(_ => throw new InvalidOperationException("Unexpected HTTP"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (capability == "image")
                await provider.ImageRequest(new() { Model = "creora-image", Prompt = "A sea" }, cancellation.Token);
            else if (capability == "video")
                await provider.StartVideoOperation(new() { Model = "creora-video", Prompt = "A sea" }, cancellation.Token);
            else
                await provider.GetVideoOperationStatus("video_abc", cancellation.Token);
        });
    }

    private static ChatCompletionOptions ChatOptions(string model)
    {
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"placeholder","temperature":0.2,
             "messages":[{"role":"user","content":[
               {"type":"text","text":"Describe this"},
               {"type":"image_url","image_url":{"url":"https://input.example/a.png?sig=a%2Bb&x=1"}}],"FutureMessageField":7}]}
            """)!;
        options.Model = model;
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RawOptions)!;
        options.TopP = 0.8;
        raw["seed"] = Element("null");
        raw["stream"] = Element("false");
        options.AdditionalProperties = raw;
        return options;
    }

    private static Dictionary<string, JsonElement> Options() => new() { ["creora"] = Element(RawOptions) };
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static JsonElement Body(HttpRequestMessage request) => Element(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

    private static void AssertRawOptions(JsonElement payload)
    {
        foreach (var property in Element(RawOptions).EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
    }

    private static CreoraProvider Create(Func<HttpRequestMessage, HttpResponseMessage> handler, string? key = "test-key")
        => new(new Keys(key), new Clients(handler), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-request-id", "request-123");
        return response;
    }

    private static HttpResponseMessage Sse(string data)
        => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };

    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "creora" ? key : null;
    }

    private sealed class Clients(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(handler));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("creora.ai", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(handler(request));
        }
    }
}
