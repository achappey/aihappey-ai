using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Xpectrum;
using AIHappey.HeaderAuth;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Xpectrum;

public sealed class XpectrumProviderTests
{
    private const string Agent = "e040c4d0-9b1e-4718-aaf9-eeb2d450b467";
    private const string RawOptions = """
        {"Future_Field":{"KeepCASE":[true,2.5,null]},"explicitNull":null,"timezone":"Asia/Kolkata",
         "variables":{"language":"en","count":3},"channel":"chat","channel_metadata":{"Source":"crm"}}
        """;
    private const string Citation = """
        {"object":"citation","knowledge_id":"k1","knowledge_name":"Help center","document_id":"d1",
         "document_name":"opening-hours.md","chunk_id":"c1","content":"We are open 9am-6pm.","score":0.91,"position":1}
        """;
    private static readonly string ChatResult = $$"""
        {"id":"chatcmpl-1","object":"chat.completion","created":1754900000,"model":"{{Agent}}",
         "choices":[{"index":0,"message":{"role":"assistant","content":"We are open 9am-6pm."},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":412,"completion_tokens":28,"total_tokens":440},
         "thread_id":"thread-1","run_id":"run-1","mode":"chatbot","citations":[{{Citation}}]}
        """;

    [Fact]
    public void Registration_is_scoped_and_header_key_is_resolved()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        var registration = Assert.Single(services,
            item => !item.IsKeyedService && item.ImplementationType == typeof(XpectrumProvider));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Contains(services, item => item.IsKeyedService && item.ServiceType == typeof(IModelProvider)
            && Equals(item.ServiceKey, "xpectrum"));
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Xpectrum-Key"] = "header-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("header-key", resolver.Resolve("xpectrum"));
    }

    [Fact]
    public async Task Discovery_preserves_the_single_native_agent_and_is_cached()
    {
        var calls = new List<HttpRequestMessage>();
        var provider = Create(request => { calls.Add(request); return Route(request, "chatbot"); });
        var model = Assert.Single(await provider.ListModels());
        await provider.ListModels();
        Assert.Single(calls);
        Assert.Equal(HttpMethod.Get, calls[0].Method);
        Assert.Equal("/v1/models", calls[0].RequestUri!.AbsolutePath);
        Assert.Equal($"xpectrum/{Agent}", model.Id);
        Assert.Equal("Support Assistant", model.Name);
        Assert.Equal("Answers questions.", model.Description);
        Assert.Equal("xpectrum", model.OwnedBy);
        Assert.Equal(1750000000, model.Created);
        Assert.Equal("language", model.Type);
        Assert.Contains("chatbot", model.Tags!);

        var keyless = Create(_ => throw new InvalidOperationException("no request expected"), key: null);
        Assert.Empty(await keyless.ListModels());
    }

    [Fact]
    public async Task Blocking_chat_sends_only_last_user_message_and_preserves_extensions()
    {
        JsonElement body = default;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            body = Body(request);
            return Json(ChatResult);
        });
        var options = ChatOptions(Messages(("system", "Be nice"), ("user", "Hi"), ("assistant", "Hello"), ("user", "Opening hours?")));
        options.User = "standard-user";
        var completion = await provider.CompleteChatAsync(options);

        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Opening hours?", message.GetProperty("content").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal(Agent, body.GetProperty("model").GetString());
        Assert.Equal("standard-user", body.GetProperty("user").GetString());
        Assert.Equal("thread-0", body.GetProperty("thread_id").GetString());
        AssertRawOptions(body);
        Assert.False(body.TryGetProperty("unused_provider", out _));

        var result = JsonSerializer.SerializeToElement(completion);
        Assert.Equal($"xpectrum/{Agent}", completion.Model);
        Assert.Equal("thread-1", result.GetProperty("thread_id").GetString());
        Assert.Equal("run-1", result.GetProperty("run_id").GetString());
        Assert.Equal(440, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        var metadata = result.GetProperty("providerMetadata").GetProperty("xpectrum");
        Assert.Equal("chatbot", metadata.GetProperty("mode").GetString());
        Assert.Equal("thread-1", metadata.GetProperty("thread_id").GetString());
        Assert.True(JsonElement.DeepEquals(Element(Citation), metadata.GetProperty("citations")[0]));
    }

    [Fact]
    public async Task Unset_standard_values_do_not_erase_native_options()
    {
        JsonElement body = default;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            body = Body(request);
            return Json(ChatResult);
        });
        var options = ChatOptions(Messages(("user", "Hi")));
        await provider.CompleteChatAsync(options);
        Assert.Equal("raw-user", body.GetProperty("user").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("explicitNull").ValueKind);
    }

    [Fact]
    public async Task Autonomous_agents_aggregate_the_native_stream_for_blocking_calls()
    {
        var requests = new List<JsonElement>();
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "agent");
            requests.Add(Body(request));
            return Sse(StreamBody);
        });
        var completion = await provider.CompleteChatAsync(ChatOptions(Messages(("user", "Hi"))));
        Assert.True(Assert.Single(requests).GetProperty("stream").GetBoolean());
        var result = JsonSerializer.SerializeToElement(completion);
        Assert.Equal("We are open", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("thread-1", result.GetProperty("thread_id").GetString());
        Assert.Equal(440, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal("agent", result.GetProperty("providerMetadata").GetProperty("xpectrum").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Blocking_rejection_falls_back_to_streaming()
    {
        var streams = new List<bool>();
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            var stream = Body(request).GetProperty("stream").GetBoolean();
            streams.Add(stream);
            return stream
                ? Sse(StreamBody)
                : Json("""{"error":{"message":"Agent Chat App does not support blocking mode","type":"invalid_request_error"}}""", HttpStatusCode.BadRequest);
        });
        var completion = await provider.CompleteChatAsync(ChatOptions(Messages(("user", "Hi"))));
        Assert.Equal([false, true], streams);
        Assert.Equal("We are open", JsonSerializer.SerializeToElement(completion).GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Streaming_is_native_sse_with_pings_threads_citations_and_usage()
    {
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            return Sse(StreamBody);
        });
        var chunks = new List<ChatCompletionUpdate>();
        await foreach (var chunk in provider.CompleteChatStreamingAsync(ChatOptions(Messages(("user", "Hi")))))
            chunks.Add(chunk);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal($"xpectrum/{Agent}", chunk.Model));
        var first = JsonSerializer.SerializeToElement(chunks[0]);
        Assert.Equal("thread-1", first.GetProperty("thread_id").GetString());
        Assert.Equal("run-1", first.GetProperty("run_id").GetString());
        Assert.Equal("chatbot", first.GetProperty("mode").GetString());
        var last = JsonSerializer.SerializeToElement(chunks[2]);
        Assert.Equal(440, last.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.True(JsonElement.DeepEquals(Element(Citation), last.GetProperty("citations")[0]));
    }

    [Fact]
    public async Task Abandoned_streams_cancel_the_server_run_with_the_same_user()
    {
        var cancels = new ConcurrentQueue<(string Path, JsonElement Body)>();
        var provider = Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/chat/completions") return Sse(StreamBody);
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                cancels.Enqueue((path, Body(request)));
                return Json("""{"id":"run-1","object":"run.cancelled"}""");
            }
            return Route(request, "chatbot");
        });
        await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions(Messages(("user", "Hi")))))
            break;
        var cancel = Assert.Single(cancels);
        Assert.Equal("/v1/runs/run-1/cancel", cancel.Path);
        Assert.Equal("raw-user", cancel.Body.GetProperty("user").GetString());

        cancels.Clear();
        await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions(Messages(("user", "Hi"))))) { }
        Assert.Empty(cancels);
    }

    [Fact]
    public async Task Stream_error_chunks_and_rate_limits_surface_native_details()
    {
        var provider = Create(request => request.RequestUri!.AbsolutePath == "/v1/chat/completions"
            ? Sse("data: {\"error\":{\"message\":\"Quota exceeded\",\"code\":\"quota_exceeded\"}}\n\n")
            : Route(request, "chatbot"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions(Messages(("user", "Hi"))))) { }
        });
        Assert.Contains("quota_exceeded", error.Message);
        Assert.Equal("quota_exceeded", error.Data["code"]);

        var limited = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            var response = Json("""{"code":"rate_limit_exceeded","message":"Too many requests","status":429}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
            return response;
        });
        var rate = await Assert.ThrowsAsync<HttpRequestException>(() => limited.CompleteChatAsync(ChatOptions(Messages(("user", "Hi")))));
        Assert.Equal(HttpStatusCode.TooManyRequests, rate.StatusCode);
        Assert.Equal("rate_limit_exceeded", rate.Data["code"]);
        Assert.Equal(7d, rate.Data["retryAfterSeconds"]);
        Assert.Contains("Too many requests", rate.Message);

        var wrongMode = Create(request => request.RequestUri!.AbsolutePath == "/v1/chat/completions"
            ? Json("""{"error":{"message":"Wrong app type","type":"invalid_request_error","code":"model_not_found"}}""", HttpStatusCode.NotFound)
            : Route(request, "chatbot"));
        var notFound = await Assert.ThrowsAsync<HttpRequestException>(() => wrongMode.CompleteChatAsync(ChatOptions(Messages(("user", "Hi")))));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("model_not_found", notFound.Data["code"]);
    }

    [Fact]
    public async Task Unified_conversations_continue_threads_and_map_citations()
    {
        var bodies = new List<JsonElement>();
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            bodies.Add(Body(request));
            return Json(ChatResult);
        });
        var first = await provider.ExecuteUnifiedAsync(Unified([UserItem("My name is Ada.")],
            new { user = "user-123", Custom = (string?)null }));
        var items = first.Output!.Items!;
        var assistant = items.First(item => item.Role == "assistant");
        var tool = Assert.IsType<AIToolCallContentPart>(assistant.Content![0]);
        Assert.Equal("xpectrum_thread", tool.ToolName);
        Assert.True(tool.ProviderExecuted);
        Assert.Contains(items, item => item.Type == "source-document");
        Assert.Equal(JsonValueKind.Null, bodies[0].GetProperty("Custom").ValueKind);

        var history = new List<AIInputItem>
        {
            UserItem("My name is Ada."),
            new() { Role = "assistant", Content = [tool, new AITextContentPart { Type = "text", Text = "Hi Ada" }] },
            UserItem("What is my name?")
        };
        await provider.ExecuteUnifiedAsync(Unified(history, null));
        var second = bodies[1];
        Assert.Equal("thread-1", second.GetProperty("thread_id").GetString());
        Assert.Equal("user-123", second.GetProperty("user").GetString());
        Assert.Equal("What is my name?", Assert.Single(second.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());

        // An explicit native null starts a new thread instead of continuing the recovered one.
        await provider.ExecuteUnifiedAsync(Unified(history, new { thread_id = (string?)null }));
        Assert.Equal(JsonValueKind.Null, bodies[2].GetProperty("thread_id").ValueKind);
    }

    [Fact]
    public async Task Unified_stream_emits_thread_identity_and_source_documents()
    {
        var provider = Create(request => request.RequestUri!.AbsolutePath == "/v1/chat/completions"
            ? Sse(StreamBody) : Route(request, "chatbot"));
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(Unified([UserItem("Hi")], null)))
            events.Add(item);
        Assert.Single(events, item => item.Event.Type == "tool-input-available");
        var output = Assert.Single(events, item => item.Event.Type == "tool-output-available");
        Assert.Equal("xpectrum_thread", Assert.IsType<AIToolOutputAvailableEventData>(output.Event.Data).ToolName);
        Assert.Single(events, item => item.Event.Type == "source-document");
        Assert.Contains(events, item => item.Event.Type == "text-delta");
        Assert.Contains(events, item => item.Event.Type == "finish");
    }

    [Fact]
    public async Task Vercel_stream_uses_the_resolved_end_user()
    {
        JsonElement body = default;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            body = Body(request);
            return Sse(StreamBody);
        }, endUser: "resolved-user");
        var request = new ChatRequest
        {
            Id = "chat-1", Model = $"xpectrum/{Agent}",
            Messages = [new UIMessage { Id = "u1", Role = Role.user, Parts = [new TextUIPart { Text = "Hi" }] }]
        };
        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(request)) parts.Add(part);
        Assert.NotEmpty(parts);
        Assert.Equal("resolved-user", body.GetProperty("user").GetString());
    }

    [Fact]
    public async Task Files_are_forwarded_as_urls_and_attachments_without_downloading()
    {
        JsonElement body = default;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return Route(request, "chatbot");
            body = Body(request);
            return Json(ChatResult);
        });
        var item = new AIInputItem
        {
            Role = "user",
            Content =
            [
                new AITextContentPart { Type = "text", Text = "Summarise" },
                new AIFileContentPart { Type = "file", MediaType = "image/png", Data = "https://files.example/photo.png" },
                new AIFileContentPart { Type = "file", MediaType = "application/pdf", Data = "https://files.example/report.pdf?sig=a%2Bb" }
            ]
        };
        await provider.ExecuteUnifiedAsync(Unified([item],
            new { attachments = new object[] { new { type = "image", file_id = "f1" } } }));
        var content = body.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("https://files.example/photo.png", content[1].GetProperty("image_url").GetProperty("url").GetString());
        var attachments = body.GetProperty("attachments");
        Assert.Equal("f1", attachments[0].GetProperty("file_id").GetString());
        Assert.Equal("document", attachments[1].GetProperty("type").GetString());
        Assert.Equal("https://files.example/report.pdf?sig=a%2Bb", attachments[1].GetProperty("url").GetString());

        var inline = new AIInputItem
        {
            Role = "user",
            Content = [new AIFileContentPart { Type = "file", MediaType = "application/pdf", Data = "JVBERi0=" }]
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(Unified([inline], null)));
    }

    [Fact]
    public async Task Workflow_apps_run_blocking_and_report_failures()
    {
        JsonElement body = default;
        var status = "succeeded";
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/runs") return Route(request, "workflow");
            body = Body(request);
            return Json($$"""
                {"id":"wf-run","object":"run","status":"{{status}}","variables":{"topic":"q"},"outputs":{"summary":"Done"},
                 "error":{{(status == "succeeded" ? "null" : "\"Step 2 failed\"")}},"steps":4,"tokens":1832,"elapsed_seconds":6.42,
                 "created_at":1754900000,"finished_at":1754900006}
                """);
        });
        var options = ChatOptions(Messages(("user", "ignored")));
        var completion = await provider.CompleteChatAsync(options);
        Assert.Equal("raw-user", body.GetProperty("user").GetString());
        Assert.Equal(3, body.GetProperty("variables").GetProperty("count").GetInt32());
        Assert.False(body.GetProperty("stream").GetBoolean());
        var result = JsonSerializer.SerializeToElement(completion);
        Assert.Equal("Done", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(1832, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        var metadata = result.GetProperty("providerMetadata").GetProperty("xpectrum");
        Assert.Equal("wf-run", metadata.GetProperty("run_id").GetString());
        Assert.Equal(4, metadata.GetProperty("run").GetProperty("steps").GetInt32());

        status = "failed";
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(ChatOptions(Messages(("user", "x")))));
        Assert.Contains("Step 2 failed", failure.Message);

        var noUser = new ChatCompletionOptions { Model = $"xpectrum/{Agent}", Messages = Messages(("user", "x")) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteChatAsync(noUser));
    }

    [Fact]
    public async Task Workflow_streams_map_progress_events_and_cancel_when_abandoned()
    {
        var cancels = new ConcurrentQueue<string>();
        var provider = Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/cancel", StringComparison.Ordinal)) { cancels.Enqueue(path); return Json("{}"); }
            if (path != "/v1/runs") return Route(request, "workflow");
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            return Sse("""
                data: {"id":"wf-run","object":"run.started","created_at":1754900000}

                : ping

                data: {"id":"s1","object":"step.started","title":"Retrieve","type":"knowledge-retrieval","index":1,"created_at":1754900000}

                data: {"id":"wf-run","object":"run.completed","status":"succeeded","outputs":{"summary":"Done"},"steps":4,"tokens":10,"created_at":1754900000}

                data: [DONE]

                """);
        });
        var chunks = new List<ChatCompletionUpdate>();
        await foreach (var chunk in provider.CompleteChatStreamingAsync(ChatOptions(Messages(("user", "x")))))
            chunks.Add(chunk);
        Assert.Equal(3, chunks.Count);
        var step = JsonSerializer.SerializeToElement(chunks[1]);
        Assert.Equal("step.started", step.GetProperty("providerMetadata").GetProperty("xpectrum").GetProperty("event").GetProperty("object").GetString());
        Assert.Equal("wf-run", step.GetProperty("run_id").GetString());
        Assert.Equal("Done", chunks[2].Choices.Single().Delta.Content);
        Assert.Empty(cancels);

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(Unified([UserItem("x")], new { user = "u", variables = new { topic = "q" } })))
            events.Add(item);
        Assert.Contains(events, item => item.Event.Type == "data-xpectrum-run");

        await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions(Messages(("user", "x")))))
            break;
        Assert.Equal("/v1/runs/wf-run/cancel", Assert.Single(cancels));
    }

    [Fact]
    public async Task Realtime_tokens_preserve_livekit_details_and_keep_the_key_server_side()
    {
        JsonElement? body = null;
        var native = """
            {"token":"lk-token","room_name":"room-1","agent_name":"Support","unique_id":"u1","participant_identity":"p1",
             "participant_name":"Caller","livekit_url":"wss://voice.example.livekit.cloud","expires_in":"3600"}
            """;
        var response = native;
        var provider = Create(request =>
        {
            Assert.Equal("/v1/voice/tokens/generate", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
            body = request.Content is null ? null : Body(request);
            return Json(response);
        });
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = await provider.CreateRealtimeTokenAsync($"xpectrum/{Agent}", new RealtimeTokenRequest
        {
            SessionConfig = new() { ProviderOptions = new() { ["xpectrum"] = Element("""{"Metadata":{"x":null},"room":"r"}""") } }
        });
        Assert.Equal("lk-token", token.Token);
        Assert.Equal("wss://voice.example.livekit.cloud", token.Url);
        Assert.InRange(token.ExpiresAt!.Value, before + 3599, before + 3602);
        Assert.True(JsonElement.DeepEquals(Element("""{"Metadata":{"x":null},"room":"r"}"""), body!.Value));

        var legacy = await provider.GetRealtimeToken(new RealtimeRequest { Model = $"xpectrum/{Agent}" });
        Assert.Null(body);
        Assert.Equal("lk-token", legacy.Value);
        Assert.True(JsonElement.DeepEquals(Element(native), legacy.ProviderMetadata!["xpectrum"]));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateRealtimeTokenAsync(Agent,
            new RealtimeTokenRequest { SessionConfig = new() { Voice = "alloy" } }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateRealtimeSessionAsync(Agent,
            new RealtimeSessionRequest { Sdp = "v=0" }));

        response = native.Replace("lk-token", "test-key");
        var leak = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(new RealtimeRequest { Model = Agent }));
        Assert.DoesNotContain("test-key", leak.Message);
        response = native.Replace("wss://voice.example.livekit.cloud", "ws://insecure.example");
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(new RealtimeRequest { Model = Agent }));
    }

    [Fact]
    public async Task Realtime_provisioning_errors_do_not_echo_upstream_bodies()
    {
        var provider = Create(_ => Json("""{"code":"voice_disabled","message":"bad key test-key"}""", HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetRealtimeToken(new RealtimeRequest { Model = Agent }));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("voice_disabled", error.Data["code"]);
        Assert.DoesNotContain("test-key", error.Message);
    }

    private static readonly string StreamBody = $$"""
        : ping

        data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1754900000,"model":"{{Agent}}","thread_id":"thread-1","run_id":"run-1","choices":[{"index":0,"delta":{"role":"assistant","content":"We"},"finish_reason":null}]}

        data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1754900000,"model":"{{Agent}}","thread_id":"thread-1","run_id":"run-1","choices":[{"index":0,"delta":{"content":" are open"},"finish_reason":null}]}

        : ping

        data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1754900000,"model":"{{Agent}}","thread_id":"thread-1","run_id":"run-1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":412,"completion_tokens":28,"total_tokens":440},"citations":[{{Citation.ReplaceLineEndings(" ")}}]}

        data: [DONE]

        """;

    private static HttpResponseMessage Route(HttpRequestMessage request, string mode)
    {
        Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
        return Json($$"""
            {"object":"list","data":[{"id":"{{Agent}}","object":"model","created":1750000000,"owned_by":"xpectrum",
             "name":"Support Assistant","description":"Answers questions.","mode":"{{mode}}","title":"Support",
             "greeting":"Hi!","starter_questions":["Hours?"]}]}
            """);
    }

    private static List<ChatMessage> Messages(params (string Role, string Text)[] messages)
        => messages.Select(message => JsonSerializer.Deserialize<ChatMessage>(
            JsonSerializer.SerializeToElement(new { role = message.Role, content = message.Text }))!).ToList();

    private static ChatCompletionOptions ChatOptions(List<ChatMessage> messages)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RawOptions)!;
        raw["user"] = Element("\"raw-user\"");
        raw["thread_id"] = Element("\"thread-0\"");
        return AIHappey.Tests.Contracts.NativeTestOptions.Apply(new ChatCompletionOptions
        {
            Model = $"xpectrum/{Agent}",
            Messages = messages,
            AdditionalProperties = new()
            {
            }
        }, raw);
    }

    private static AIInputItem UserItem(string text)
        => new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = text }] };

    private static AIRequest Unified(List<AIInputItem> items, object? raw)
        => new()
        {
            ProviderId = "xpectrum", Model = $"xpectrum/{Agent}", Input = new() { Items = items },
            Metadata = raw is null ? null : new() { ["xpectrum"] = JsonSerializer.SerializeToElement(raw) }
        };

    private static void AssertRawOptions(JsonElement payload)
    {
        foreach (var property in Element(RawOptions).EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
    }

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static JsonElement Body(HttpRequestMessage request) => Element(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

    private static XpectrumProvider Create(Func<HttpRequestMessage, HttpResponseMessage> handler, string? key = "test-key", string? endUser = null)
        => new(new Keys(key), new EndUser(endUser), new Clients(handler), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string data)
        => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };

    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "xpectrum" ? key : null;
    }

    private sealed class EndUser(string? user) : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => user;
    }

    private sealed class Clients(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(handler));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("cloud.xpectrum.dev", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(handler(request));
        }
    }
}
