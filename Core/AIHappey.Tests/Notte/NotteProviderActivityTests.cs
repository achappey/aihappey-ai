using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Notte;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Notte;

public class NotteProviderActivityTests
{
    private const string Snapshot = """
        {
          "status":"closed", "success":true, "answer":"Final answer",
          "steps":[
            {"type":"agent_step_start","value":{"step_number":0}},
            {"type":"observation","value":{"space":{"description":"Long page observation"}}},
            {"type":"agent_completion","value":{
              "state":{"memory":"Useful memory","next_goal":"Navigate to the page","page_summary":"Waiting page","previous_goal_eval":"Ready to navigate"},
              "action":{"type":"goto","url":"https://example.com"}}},
            {"type":"execution_result","value":{"action":{"type":"goto","url":"https://example.com","timeout":5000},"success":true,"message":"Navigated"}},
            {"type":"agent_step_start","value":{"step_number":1}},
            {"type":"observation","value":"Another long observation"},
            {"type":"agent_completion","value":{
              "state":{"memory":"Duplicate final memory","next_goal":"Final answer"},
              "action":{"type":"completion","answer":"Final answer","success":true}}},
            {"type":"execution_result","value":{"action":{"type":"completion","answer":"Final answer"},"success":true,"message":"Completed"}}
          ]
        }
        """;

    [Fact]
    public async Task Stream_keeps_progress_and_browser_tools_but_not_observations_or_completion()
    {
        using var handler = new Handler(Snapshot);
        using var client = new HttpClient(handler);
        var provider = Provider(client);
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(Request())) events.Add(item);

        var reasoning = Assert.Single(events.Where(e => e.Event.Type == "reasoning-delta"));
        var text = Assert.IsType<AIReasoningDeltaEventData>(reasoning.Event.Data).Delta;
        Assert.Contains("Useful memory", text);
        Assert.Contains("Navigate to the page", text);
        Assert.Contains("Waiting page", text);
        Assert.Contains("Ready to navigate", text);
        Assert.DoesNotContain("observation", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Final answer", text);
        Assert.Single(events.Where(e => e.Event.Type == "reasoning-start"));
        Assert.Single(events.Where(e => e.Event.Type == "reasoning-end"));

        var inputs = events.Where(e => e.Event.Type == "tool-input-available").ToArray();
        Assert.Equal(2, inputs.Length); // Existing session plus the actual navigation.
        Assert.DoesNotContain(inputs, e => Assert.IsType<AIToolInputAvailableEventData>(e.Event.Data).ToolName == "notte_completion");
        var navigation = Assert.Single(inputs, e => Assert.IsType<AIToolInputAvailableEventData>(e.Event.Data).ToolName == "notte_goto");
        Assert.Contains(events, e => e.Event.Type == "tool-output-available" && e.Event.Id == navigation.Event.Id);
        Assert.Equal("Final answer", Assert.IsType<AITextDeltaEventData>(Assert.Single(events, e => e.Event.Type == "text-delta").Event.Data).Delta);
        Assert.Equal("stop", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).FinishReason);

        // Native activity remains available without becoming visible UI cards.
        Assert.Contains(events, e => e.Event.Type == "data-notte-agent-status"
            && JsonSerializer.Serialize(Assert.IsType<AIDataEventData>(e.Event.Data).Data).Contains("Long page observation"));
        var ui = events.SelectMany(e => e.Event.ToUIMessagePart("notte")).ToArray();
        Assert.Single(ui.OfType<ReasoningDeltaUIPart>());
        Assert.DoesNotContain(ui.OfType<ToolCallPart>(), p => p.ToolName == "notte_completion");
    }

    [Fact]
    public async Task Unified_response_keeps_one_navigation_result_and_the_normal_final_answer()
    {
        using var handler = new Handler(Snapshot);
        using var client = new HttpClient(handler);
        var response = await Provider(client).ExecuteUnifiedAsync(Request());
        var content = Assert.Single(response.Output!.Items!).Content!;
        Assert.Equal(2, content.OfType<AIToolCallContentPart>().Count());
        var navigation = Assert.Single(content.OfType<AIToolCallContentPart>(), p => p.ToolName == "notte_goto");
        Assert.Equal("output-available", navigation.State);
        Assert.DoesNotContain(content.OfType<AIToolCallContentPart>(), p => p.ToolName == "notte_completion");
        Assert.DoesNotContain("Final answer", Assert.Single(content.OfType<AIReasoningContentPart>()).Text!);
        Assert.Equal("Final answer", Assert.Single(content.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task Completion_result_without_planned_action_does_not_create_an_orphan_card()
    {
        const string snapshot = """
            {"status":"closed","answer":"Final answer","steps":[
              {"type":"execution_result","value":{"action":{"type":"completion","answer":"Final answer"},"success":true}}
            ]}
            """;
        using var handler = new Handler(snapshot);
        using var client = new HttpClient(handler);
        var response = await Provider(client).ExecuteUnifiedAsync(Request());
        var content = Assert.Single(response.Output!.Items!).Content!;
        Assert.Equal("create_notte_session", Assert.Single(content.OfType<AIToolCallContentPart>()).ToolName);
        Assert.Empty(content.OfType<AIReasoningContentPart>());
        Assert.Equal("Final answer", Assert.Single(content.OfType<AITextContentPart>()).Text);
    }

    private static AIRequest Request() => new() { ProviderId = "notte", Model = "agent", Input = new() { Text = "Task" } };
    private static NotteProvider Provider(HttpClient client)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Factory(client));
    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(string snapshot) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/sessions/start" => "{\"session_id\":\"s1\",\"status\":\"active\"}",
                "/agents/start" => "{\"agent_id\":\"a1\"}",
                "/agents/a1" => snapshot,
                "/sessions/s1/files" => "{\"files\":[],\"total\":0}",
                _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
