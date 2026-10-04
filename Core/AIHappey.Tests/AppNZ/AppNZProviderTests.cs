using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AppNZ;
using AIHappey.Messages;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.AppNZ;

public sealed class AppNZProviderTests
{
    [Fact]
    public async Task Models_preserve_nested_slugs_add_one_agent_and_cache_per_key()
    {
        var calls = 0;
        var keys = new KeyResolver();
        var provider = Create(async request =>
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            calls++;
            await Task.Yield();
            return JsonResponse(new { data = new[]
            {
                new { id = "anthropic/claude", owned_by = "anthropic", type = "language" },
                new { id = "fal-ai/veo3.1/fast", owned_by = "fal", type = "video" }
            }});
        }, keys);

        var models = (await provider.ListModels()).ToList();
        Assert.Contains(models, m => m.Id == "appnz/anthropic/claude");
        Assert.Contains(models, m => m.Id == "appnz/fal-ai/veo3.1/fast" && m.Type == "video");
        Assert.Single(models.Where(m => m.Id == "appnz/agent"));
        Assert.Contains(models, m => m.Id == "appnz/app/auto-image" && m.Type == "image");
        await provider.ListModels();
        Assert.Equal(1, calls);
        keys.Key = "other-key";
        await provider.ListModels();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Chat_preserves_raw_options_nested_model_and_unknown_response_fields()
    {
        var provider = Create(async request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            var payload = await Body(request);
            Assert.Equal("anthropic/claude", payload.GetProperty("model").GetString());
            Assert.Equal("config", payload.GetProperty("routing_strategy").GetString());
            Assert.Equal(17, payload.GetProperty("future_option").GetInt32());
            Assert.False(payload.TryGetProperty("providerMetadata", out _));
            return ChatResponse("anthropic/claude");
        });
        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "anthropic/claude",
            Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("hello") }],
            Headers = new() { ["Authorization"] = "Bearer wrong-key" },
            Metadata = new() { ["appnz"] = new { routing_strategy = "config", future_option = 17 } }
        });
        Assert.Equal("appnz/anthropic/claude", response.Model);
        Assert.Equal("preserved", response.AdditionalProperties!["upstream_field"].GetString());
    }

    [Fact]
    public async Task Chat_stream_uses_compatible_sse()
    {
        var provider = Create(async request =>
        {
            var payload = await Body(request);
            Assert.True(payload.GetProperty("stream").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"app/auto\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        });
        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new() { Model = "app/auto" }))
            updates.Add(update);
        Assert.Equal("appnz/app/auto", Assert.Single(updates).Model);
    }

    [Fact]
    public async Task Native_messages_preserve_thinking_cache_controls_and_filter_auth_headers()
    {
        var provider = Create(async request =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.True(request.Headers.Contains("anthropic-beta"));
            var payload = await Body(request);
            Assert.Equal("enabled", payload.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("ephemeral", payload.GetProperty("cache_control").GetProperty("type").GetString());
            return JsonResponse(new { id = "m1", type = "message", role = "assistant", model = "claude", content = new[] { new { type = "text", text = "hello" } }, stop_reason = "end_turn", usage = new { input_tokens = 1, output_tokens = 1 } });
        });
        var request = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"claude","max_tokens":100,"messages":[{"role":"user","content":"hello"}],"thinking":{"type":"enabled","budget_tokens":50},"cache_control":{"type":"ephemeral"}}
            """, JsonSerializerOptions.Web)!;
        var result = await provider.MessagesAsync(request, new()
        {
            ["authorization"] = "Bearer wrong-key", ["x-api-key"] = "wrong-key", ["anthropic-beta"] = "test-beta"
        });
        Assert.Equal("appnz/claude", result.Model);
    }

    [Fact]
    public async Task Every_agent_request_launches_fresh_task_and_preserves_raw_options_and_results()
    {
        var launches = 0;
        var provider = Create(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/agents/tasks", request.RequestUri!.AbsolutePath);
            var payload = await Body(request);
            Assert.Equal("latest prompt", payload.GetProperty("prompt").GetString());
            Assert.Equal("app/auto-code", payload.GetProperty("model").GetString());
            Assert.Equal("acme/repo", payload.GetProperty("repo").GetString());
            Assert.Equal(123, payload.GetProperty("future_field").GetInt32());
            Assert.False(payload.TryGetProperty("taskId", out _));
            return TaskResponse("task-" + ++launches, "review");
        });
        var request = AgentRequest(new() { ["appnz"] = new { repo = "acme/repo", model = "app/auto-code", future_field = 123, taskId = "old-task" } });
        var first = await provider.ExecuteUnifiedAsync(request);
        var second = await provider.ExecuteUnifiedAsync(request);
        Assert.Equal(2, launches);
        var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(first.Output!.Items!).Content!));
        Assert.True(tool.ProviderExecuted);
        Assert.Equal("appnz_execute_task", tool.ToolName);
        var result = Assert.IsType<CallToolResult>(tool.Output);
        Assert.Equal("task-1", result.StructuredContent!.Value.GetProperty("task").GetProperty("id").GetString());
        Assert.Equal("preserved", result.StructuredContent.Value.GetProperty("future_response").GetString());
        Assert.NotEqual(tool.ToolCallId, Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(second.Output!.Items!).Content!)).ToolCallId);
    }

    [Fact]
    public async Task Agent_stream_polls_new_task_and_emits_preliminary_and_terminal_outputs()
    {
        var polls = 0;
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/agents/tasks", request.RequestUri!.AbsolutePath);
                return Task.FromResult(TaskResponse("new-task", "running"));
            }
            Assert.Equal("/api/agents/tasks/new-task", request.RequestUri!.AbsolutePath);
            polls++;
            return Task.FromResult(TaskResponse("new-task", "done"));
        });
        var events = new List<AIStreamEvent>();
        await foreach (var part in provider.StreamUnifiedAsync(AgentRequest())) events.Add(part);
        Assert.Equal(1, polls);
        Assert.Single(events.Where(e => e.Event.Type == "tool-input-available"));
        var outputs = events.Select(e => e.Event.Data).OfType<AIToolOutputAvailableEventData>().ToList();
        Assert.True(outputs[0].Preliminary);
        Assert.False(outputs[^1].Preliminary);
        Assert.Equal("finish", events[^1].Event.Type);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Failed_agent_task_is_not_reported_as_success(string status)
    {
        var provider = Create(_ => Task.FromResult(TaskResponse("t1", status)));
        var result = await provider.ExecuteUnifiedAsync(AgentRequest());
        Assert.Equal("failed", result.Status);
        var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(result.Output!.Items!).Content!));
        Assert.True(Assert.IsType<CallToolResult>(tool.Output).IsError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20001)]
    public async Task Invalid_agent_prompt_is_rejected_before_launch(int length)
    {
        var provider = Create(_ => throw new Xunit.Sdk.XunitException("Task must not be launched."));
        var request = new AIRequest
        {
            ProviderId = "appnz",
            Model = "agent", Input = new AIInput { Items = [new AIInputItem
            {
                Role = "user", Content = [new AITextContentPart { Type = "text", Text = new string('x', length) }]
            }] }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(request));
    }

    [Fact]
    public async Task Disposing_agent_stream_cancels_only_the_new_task()
    {
        var cancelled = false;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/cancel"))
            {
                Assert.Equal("/api/agents/tasks/new-task/cancel", request.RequestUri.AbsolutePath);
                cancelled = true;
                return Task.FromResult(TaskResponse("new-task", "cancelled"));
            }
            return Task.FromResult(TaskResponse("new-task", "running"));
        });
        await using (var enumerator = provider.StreamUnifiedAsync(AgentRequest()).GetAsyncEnumerator())
            Assert.True(await enumerator.MoveNextAsync());
        Assert.True(cancelled);
    }

    [Fact]
    public async Task Agent_result_survives_chat_responses_messages_and_ui_mappings()
    {
        var launches = 0;
        var provider = Create(async request =>
        {
            Assert.Equal("/api/agents/tasks", request.RequestUri!.AbsolutePath);
            var payload = await Body(request);
            Assert.Equal("acme/repo", payload.GetProperty("repo").GetString());
            return TaskResponse("mapping-task-" + ++launches, "review");
        });
        var chat = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "agent", Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("work") }],
            Metadata = new() { ["appnz"] = new { repo = "acme/repo" } }
        });
        Assert.Contains("appnz_execute_task", JsonSerializer.Serialize(chat, JsonSerializerOptions.Web));

        var responseRequest = JsonSerializer.Deserialize<AIHappey.Responses.ResponseRequest>("""
            {"model":"agent","input":"work","metadata":{"appnz":{"repo":"acme/repo"}}}
            """, JsonSerializerOptions.Web)!;
        var response = await provider.ResponsesAsync(responseRequest);
        Assert.Equal("completed", response.Status);
        Assert.Contains("mapping-task-2", JsonSerializer.Serialize(response, JsonSerializerOptions.Web));

        var messages = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"agent","messages":[{"role":"user","content":"work"}],"metadata":{"appnz":{"repo":"acme/repo"}}}
            """, JsonSerializerOptions.Web)!;
        var messageResult = await provider.MessagesAsync(messages, []);
        Assert.Contains("mapping-task-3", JsonSerializer.Serialize(messageResult, JsonSerializerOptions.Web));

        var ui = JsonSerializer.Deserialize<ChatRequest>("""
            {"id":"c1","model":"appnz/agent","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"work"}]}],"providerMetadata":{"appnz":{"repo":"acme/repo"}}}
            """, JsonSerializerOptions.Web)!;
        var uiParts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(ui)) uiParts.Add(part);
        Assert.Contains("mapping-task-4", JsonSerializer.Serialize(uiParts, JsonSerializerOptions.Web));
        Assert.Equal(4, launches);
    }

    [Fact]
    public async Task Timeout_cancels_fresh_task_without_retry()
    {
        var launches = 0;
        var cancelled = false;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/cancel"))
            {
                cancelled = true;
                return Task.FromResult(TaskResponse("timeout-task", "cancelled"));
            }
            launches++;
            return Task.FromResult(TaskResponse("timeout-task", "running"));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(
            AgentRequest(new() { ["appnz"] = new { timeoutSeconds = 0.05 } })));
        Assert.Equal(1, launches);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task Http_task_creation_failure_is_not_retried()
    {
        var launches = 0;
        var provider = Create(_ =>
        {
            launches++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("provider rate limit")
            });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(AgentRequest()));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("provider rate limit", error.Message);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task Credentials_are_isolated_between_concurrent_operations()
    {
        var keys = new KeyResolver();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<string>();
        var provider = Create(async request =>
        {
            var key = request.Headers.Authorization!.Parameter!;
            lock (observed) observed.Add(key);
            if (key == "test-key")
            {
                entered.SetResult();
                await release.Task;
                Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            }
            return ChatResponse("app/auto");
        }, keys);
        var first = provider.CompleteChatAsync(new() { Model = "app/auto" });
        await entered.Task;
        keys.Key = "second-key";
        await provider.CompleteChatAsync(new() { Model = "app/auto" });
        release.SetResult();
        await first;
        Assert.Equal(new[] { "test-key", "second-key" }, observed);
    }

    [Theory]
    [InlineData("generation", "/v1/videos/generations")]
    [InlineData("edit", "/v1/videos/edits")]
    [InlineData("extension", "/v1/videos/extensions")]
    public async Task Video_routes_preserve_options_and_poll_without_auth_on_download(string kind, string path)
    {
        var polls = 0;
        var downloads = 0;
        var provider = Create(async request =>
        {
            if (request.RequestUri!.Host == "artifacts.example")
            {
                Assert.Null(request.Headers.Authorization);
                downloads++;
                return BinaryResponse("video/mp4");
            }
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(path, request.RequestUri.AbsolutePath);
                var payload = await Body(request);
                Assert.Equal(3, payload.GetProperty("future_option").GetInt32());
                Assert.False(payload.TryGetProperty("generationType", out _));
                if (kind != "generation") Assert.Equal("https://input.example/clip.mp4", payload.GetProperty("video").GetProperty("url").GetString());
                return JsonResponse(new { request_id = "v1", status = "pending" });
            }
            polls++;
            Assert.Equal("/v1/videos/v1", request.RequestUri.AbsolutePath);
            return JsonResponse(new { status = "completed", video = new { url = "https://artifacts.example/clip.mp4" }, future_response = 42 });
        });
        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "xai/video", Prompt = "test", Duration = kind == "edit" ? null : 5,
            InputReferences = kind == "generation" ? null : [new VideoFileUrl { Url = "https://input.example/clip.mp4" }],
            ProviderOptions = new() { ["appnz"] = JsonSerializer.SerializeToElement(new { generationType = kind, future_option = 3 }) }
        });
        var result = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus(started.Operation));
        Assert.Equal(1, polls);
        Assert.Equal(1, downloads);
        Assert.Equal(42, result.ProviderMetadata!["appnz"].GetProperty("future_response").GetInt32());
    }

    [Theory]
    [InlineData("music", "/v1/music/generations")]
    [InlineData("sfx", "/v1/audio/generations")]
    public async Task Audio_generation_uses_existing_speech_output(string kind, string endpoint)
    {
        var provider = Create(async request =>
        {
            if (request.RequestUri!.Host == "artifacts.example")
            {
                Assert.Null(request.Headers.Authorization);
                return BinaryResponse("audio/mpeg");
            }
            Assert.Equal(endpoint, request.RequestUri.AbsolutePath);
            var payload = await Body(request);
            Assert.Equal("rain", payload.GetProperty("prompt").GetString());
            Assert.Equal(7, payload.GetProperty("duration").GetInt32());
            return JsonResponse(new { audio = new { url = "https://artifacts.example/rain.mp3" }, custom = "raw" });
        });
        var result = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "audio-model", Text = "rain", ProviderOptions = new()
            {
                ["appnz"] = JsonSerializer.SerializeToElement(new { generationType = kind, duration = 7 })
            }
        });
        Assert.Equal("mp3", result.Audio.Format);
        Assert.Equal("raw", result.ProviderMetadata!["appnz"].GetProperty("raw").GetProperty("custom").GetString());
    }

    [Fact]
    public async Task Compatible_embeddings_images_speech_and_transcription_work()
    {
        var provider = Create(async request =>
        {
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/embeddings":
                    var embedding = await Body(request);
                    Assert.Equal("embed/model", embedding.GetProperty("model").GetString());
                    return JsonResponse(new { @object = "list", model = "embed/model", data = new[] { new { @object = "embedding", index = 0, embedding = new[] { .1, .2 } } }, usage = new { prompt_tokens = 2, total_tokens = 2 }, custom = 9 });
                case "/v1/images/generations":
                    var image = await Body(request);
                    Assert.Equal(99, image.GetProperty("seed").GetInt32());
                    return JsonResponse(new { created = 1, data = new[] { new { b64_json = "AQID" } }, custom = 8 });
                case "/v1/audio/speech":
                    var speech = await Body(request);
                    Assert.Equal("raw-value", speech.GetProperty("future_option").GetString());
                    return BinaryResponse("audio/mpeg");
                case "/v1/audio/transcriptions":
                    Assert.IsType<MultipartFormDataContent>(request.Content);
                    return JsonResponse(new { text = "hello", language = "en", duration = 1.5, segments = new[] { new { id = 0, seek = 0, text = "hello", start = 0, end = 1.5, tokens = new[] { 1 }, temperature = 0, avg_logprob = 0, compression_ratio = 1, no_speech_prob = 0 } }, custom = 7 });
                default:
                    throw new Xunit.Sdk.XunitException("Unexpected endpoint.");
            }
        });
        var embeddingResult = await provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = "embed/model", Input = JsonSerializer.SerializeToElement("hello")
        });
        Assert.Equal("appnz/embed/model", embeddingResult.Model);
        var imageResult = await provider.ImageRequest(new()
        {
            Model = "gpt-image-2", Prompt = "hello", Seed = 99
        });
        Assert.Equal("AQID", Assert.Single(imageResult.Images!));
        Assert.Equal(8, imageResult.ProviderMetadata!["appnz"].GetProperty("custom").GetInt32());
        var speechResult = await provider.SpeechRequest(new()
        {
            Model = "appnz-tts", Text = "hello", ProviderOptions = new()
            {
                ["appnz"] = JsonSerializer.SerializeToElement(new { future_option = "raw-value" })
            }
        });
        Assert.Equal("audio/mpeg", speechResult.Audio.MimeType);
        var transcription = await provider.TranscriptionRequest(new()
        {
            Model = "whisper-1", Audio = "AQID", MediaType = "audio/wav"
        });
        Assert.Equal("hello", transcription.Text);
        Assert.Equal(1.5f, Assert.Single(transcription.Segments).EndSecond);
        Assert.Equal(7, transcription.ProviderMetadata!["appnz"].GetProperty("custom").GetInt32());
    }

    private static AIRequest AgentRequest(Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = "appnz", Model = "agent", Metadata = metadata,
            Input = new AIInput { Items = [
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "old prompt" }] },
                new AIInputItem { Role = "assistant", Content = [new AIToolCallContentPart
                {
                    Type = "tool-call", ToolName = "appnz_execute_task", ProviderExecuted = true,
                    Output = new { taskId = "old-task" }
                }] },
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "latest prompt" }] }
            ] }
        };

    private static AppNZProvider Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder, KeyResolver? keys = null)
        => new(keys ?? new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new ClientFactory(responder));

    private static async Task<JsonElement> Body(HttpRequestMessage request)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage TaskResponse(string id, string status)
        => JsonResponse(new { success = true, task = new { id, status, steps = new[] { new { title = "step", status, log = "raw step log" } } }, future_response = "preserved" });

    private static HttpResponseMessage ChatResponse(string model)
        => JsonResponse(new { id = "chat-1", @object = "chat.completion", created = 1, model, choices = new[] { new { index = 0, message = new { role = "assistant", content = "hello" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }, upstream_field = "preserved" });

    private static HttpResponseMessage JsonResponse(object body)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BinaryResponse(string mimeType)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue(mimeType) } }
        };

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string Key { get; set; } = "test-key";
        public string? Resolve(string provider) => Key;
    }

    private sealed class ClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
