using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AgentContainer;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AgentContainer;

public sealed class AgentContainerProviderTests
{
    [Fact]
    public async Task ListModels_exposes_conversation_and_task_for_each_agent()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/v1/agents", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return JsonResponse(new
            {
                data = new[] { new { id = "agent-1", name = "Builder", description = "Builds things", archivedAt = (string?)null, createdAt = "2026-01-01T00:00:00Z" } },
                pageInfo = new { hasMore = false, nextCursor = (string?)null }
            });
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Equal(2, models.Count);
        Assert.Contains(models, model => model.Id == "agentcontainer/agent-1/conversation");
        Assert.Contains(models, model => model.Id == "agentcontainer/agent-1/task");
    }

    [Fact]
    public async Task Task_uses_only_latest_user_text_for_instructions_and_returns_identity_tool()
    {
        JsonElement dispatched = default;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/agents/agent-1/tasks")
            {
                dispatched = Body(request);
                Assert.True(request.Headers.Contains("Idempotency-Key"));
                return JsonResponse(TaskPayload("task-1", "autonomous", "pending"), HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/tasks/task-1")
                return JsonResponse(TaskPayload("task-1", "autonomous", "hibernating"));
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/tasks/task-1/files")
                return EmptyPage();
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var response = await provider.ExecuteUnifiedAsync(Request("agentcontainer/agent-1/task",
            User("old"), User("latest"), instructions: "SYSTEM SHOULD BE IGNORED"));

        Assert.Equal("latest", dispatched.GetProperty("instructions").GetString());
        Assert.False(dispatched.TryGetProperty("message", out _));
        var content = Assert.Single(response.Output!.Items!).Content!;
        var identity = Assert.IsType<AIToolCallContentPart>(Assert.Single(content));
        Assert.Equal("dispatch_agentcontainer_task", identity.ToolName);
        var output = JsonSerializer.SerializeToElement(identity.Output, JsonSerializerOptions.Web);
        Assert.Equal("task-1", output.GetProperty("structuredContent").GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task Conversation_continuation_posts_message_and_maps_assistant_and_activity()
    {
        JsonElement sent = default;
        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/tasks/conv-1")
                return JsonResponse(TaskPayload("conv-1", "conversation", "awaiting_input", turnNumber: 1));
            if (request.Method == HttpMethod.Post && path == "/api/v1/conversations/conv-1/messages")
            {
                sent = Body(request);
                return JsonResponse(new { task = TaskPayload("conv-1", "conversation", "running", turnNumber: 2), message = Message("u2", 2, "user", "continue") });
            }
            if (request.Method == HttpMethod.Get && path == "/api/v1/conversations/conv-1/messages")
                return JsonResponse(new { data = new[] { Message("u2", 2, "user", "continue"), Message("a2", 2, "assistant", "done") }, pageInfo = new { hasMore = false, nextCursor = (string?)null } });
            if (request.Method == HttpMethod.Get && path == "/api/v1/conversations/conv-1/activities")
                return JsonResponse(new { data = new[] { new { id = "activity-1", turnNumber = 2, attemptNumber = 1, startedSequence = 1, completedSequence = 2, kind = "tool", callId = "call-1", toolKey = "shell", input = new { command = "pwd" }, inputOmittedReason = (string?)null, status = "completed", errorCode = (string?)null, startedAt = "2026-01-01T00:00:00Z", completedAt = "2026-01-01T00:00:01Z", durationMs = 1000 } }, pageInfo = new { hasMore = false, nextCursor = (string?)null } });
            if (request.Method == HttpMethod.Get && path == "/api/v1/tasks/conv-1/files")
                return EmptyPage();
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var response = await provider.ExecuteUnifiedAsync(Request(
            "agentcontainer/agent-1/conversation",
            User("continue"),
            metadata: new { taskId = "conv-1", pollIntervalMs = 1 }));

        Assert.Equal("continue", sent.GetProperty("content").GetString());
        var content = Assert.Single(response.Output!.Items!).Content!;
        Assert.DoesNotContain(content.OfType<AIToolCallContentPart>(), part => part.ToolName == "create_agentcontainer_conversation");
        Assert.Contains(content.OfType<AIToolCallContentPart>(), part => part.ToolName == "shell");
        Assert.Equal("done", Assert.Single(content.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task Hibernating_task_continuation_wakes_with_latest_message()
    {
        JsonElement wake = default;
        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/tasks/task-1")
                return JsonResponse(TaskPayload("task-1", "autonomous", "hibernating"));
            if (request.Method == HttpMethod.Post && path == "/api/v1/tasks/task-1/wake")
            {
                wake = Body(request);
                return JsonResponse(TaskPayload("task-1", "autonomous", "hibernating", result: "woken"));
            }
            if (request.Method == HttpMethod.Get && path == "/api/v1/tasks/task-1/files")
                return EmptyPage();
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var response = await provider.ExecuteUnifiedAsync(Request(
            "agentcontainer/agent-1/task",
            User("wake now"),
            metadata: new { taskId = "task-1" }));

        Assert.Equal("message", wake.GetProperty("kind").GetString());
        Assert.Equal("wake now", wake.GetProperty("message").GetString());
        Assert.Equal("woken", Assert.Single(Assert.Single(response.Output!.Items!).Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task Terminal_task_continuation_is_rejected_before_wake()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return JsonResponse(TaskPayload("task-1", "autonomous", "finished"));
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request(
            "agentcontainer/agent-1/task",
            User("again"),
            metadata: new { taskId = "task-1" })));

        Assert.Contains("terminal", exception.Message);
    }

    private static AIRequest Request(string model, AIInputItem item, string? instructions = null, object? metadata = null)
        => new()
        {
            Id = "request-1",
            ProviderId = "agentcontainer",
            Model = model,
            Instructions = instructions,
            Input = new AIInput { Items = [item] },
            Metadata = metadata is null ? null : new Dictionary<string, object?> { ["agentcontainer"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web) }
        };

    private static AIRequest Request(string model, AIInputItem first, AIInputItem second, string? instructions = null)
        => new()
        {
            Id = "request-1",
            ProviderId = "agentcontainer",
            Model = model,
            Instructions = instructions,
            Input = new AIInput { Items = [first, second] }
        };

    private static AIInputItem User(string text) => new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = text }] };

    private static object Message(string id, int turn, string role, string content) => new
    {
        id, turnNumber = turn, attemptNumber = 1, role, status = "created", content,
        files = Array.Empty<object>(), tokenUsage = (object?)null, costUsd = (decimal?)null, sequence = 1,
        createdAt = "2026-01-01T00:00:00Z"
    };

    private static object TaskPayload(string id, string mode, string status, int? turnNumber = null, string? result = null) => new
    {
        mode, id, agentId = "agent-1", agentName = "Builder", agentVersionId = "version-1",
        runtime = new { containerImage = "image", agentRuntime = "pi" }, status, turnStatus = status,
        turnNumber, sessionTtlSeconds = 3600, hibernateUntilAt = (string?)null, title = "Task",
        instructions = "instructions", runtimeRegion = "any", overrides = new { },
        execution = new { provider = "openai", model = "gpt-5", weakModel = "gpt-5-mini" },
        skills = Array.Empty<object>(), metadata = new { }, createdAt = "2026-01-01T00:00:00Z",
        startedAt = "2026-01-01T00:00:00Z", completedAt = (string?)null, result, error = (string?)null,
        costUsd = 0.01m, containerMinutes = 1m, tokenUsage = new { input_tokens = 2, output_tokens = 3 }
    };

    private static JsonElement Body(HttpRequestMessage request)
        => JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();

    private static HttpResponseMessage EmptyPage() => JsonResponse(new { data = Array.Empty<object>(), pageInfo = new { hasMore = false, nextCursor = (string?)null } });

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json) };

    private static AgentContainerProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
