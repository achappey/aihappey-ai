using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.EmpirioLabsAI;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.EmpirioLabsAI;

public class EmpirioLabsAIProviderAgentTests
{
    [Fact]
    public async Task ListModels_adds_manus_agent_exactly_once()
    {
        var provider = CreateProvider(_ => JsonResponse(new { data = Array.Empty<object>() }));
        var models = (await provider.ListModels()).ToList();
        var manus = Assert.Single(models, model => model.Id == "empiriolabsai/manus");
        Assert.Equal("agent", manus.Type);
        Assert.Contains("agent", manus.Tags ?? []);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_runs_manus_and_preserves_task_id()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/run")
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new
                {
                    task_id = "task_123", status = "completed", output = "Final report",
                    artifacts = new[] { new { type = "text_summary", url = "https://example.com/report.txt" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 }
                });
            }
            return NotFound(request);
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest());
        Assert.NotNull(body);
        Assert.Contains("\"model\":\"manus\"", body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", body, StringComparison.Ordinal);
        Assert.Equal("completed", response.Status);
        Assert.Equal("task_123", response.Metadata!["task_id"]);
        Assert.Contains(response.Output!.Items!, item => item.Type == "message");
        Assert.Contains(response.Output.Items!, item => item.Type == "file");
        var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(response.Output.Items!.First().Content!));
        var output = JsonSerializer.SerializeToElement(tool.Output, JsonSerializerOptions.Web);
        Assert.Equal("task_123", output.GetProperty("task_id").GetString());
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_recovers_task_id_from_prior_tool_output()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/agents/run")
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new { task_id = "task_existing", status = "completed", output = "Continued" });
            }
            return NotFound(request);
        });

        var request = CreateRequest("Follow up", [new AIInputItem
        {
            Role = "assistant", Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "prior", ToolName = "empiriolabs_agent_task", ProviderExecuted = true,
                Output = new { task_id = "task_existing" }
            }]
        }]);
        await provider.ExecuteUnifiedAsync(request);
        Assert.Contains("\"task_id\":\"task_existing\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_polls_accepted_task_until_terminal()
    {
        var polls = 0;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post) return JsonResponse(new { task_id = "task_poll", status = "queued" }, HttpStatusCode.Accepted);
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/task_poll")
            {
                polls++;
                return JsonResponse(new { task_id = "task_poll", status = "completed", output = "Done" });
            }
            return NotFound(request);
        });
        var response = await provider.ExecuteUnifiedAsync(CreateRequest(metadata: new { poll_interval_seconds = 0.001 }));
        Assert.Equal(1, polls);
        Assert.Equal("completed", response.Status);
    }

    [Fact]
    public async Task StreamUnifiedAsync_parses_sse_and_fetches_complete_task()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/agents/run")
                return SseResponse("event: task.running\ndata: {\"task_id\":\"task_stream\",\"status\":\"running\"}\n\nevent: output.delta\ndata: {\"task_id\":\"task_stream\",\"delta\":\"Hello \"}\n\nevent: task.completed\ndata: {\"task_id\":\"task_stream\",\"status\":\"completed\"}\n\n");
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/agents/task_stream")
                return JsonResponse(new { task_id = "task_stream", status = "completed", output = "Hello world", usage = new { total_tokens = 3 } });
            return NotFound(request);
        });
        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(CreateRequest()));
        FixtureAssertions.AssertContainsSubsequence(events.Select(item => item.Event.Type).ToList(),
            "tool-input-available", "tool-output-available", "text-start", "text-delta", "text-end", "finish");
        Assert.Contains(events, item => item.Event.Type == "text-delta" && Assert.IsType<AITextDeltaEventData>(item.Event.Data).Delta == "world");
        Assert.All(events.Where(item => item.Metadata?.ContainsKey("task_id") == true), item => Assert.Equal("task_stream", item.Metadata!["task_id"]));
    }

    private static AIRequest CreateRequest(string text = "Research this", List<AIInputItem>? preceding = null, object? metadata = null)
    {
        var items = preceding ?? [];
        items.Add(new AIInputItem
        {
            Role = "user", Content = [new AITextContentPart { Type = "text", Text = text }]
        });
        return new AIRequest
        {
            ProviderId = "empiriolabsai", Model = "empiriolabsai/manus",
            Metadata = metadata is null ? null : new Dictionary<string, object?> { ["empiriolabsai"] = metadata },
            Input = new AIInput { Items = items }
        };
    }

    private static EmpirioLabsAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.empiriolabs.ai/") }));

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SseResponse(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };

    private static HttpResponseMessage NotFound(HttpRequestMessage request)
        => new(HttpStatusCode.NotFound) { Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request); response.RequestMessage = request; return Task.FromResult(response);
        }
    }
}
