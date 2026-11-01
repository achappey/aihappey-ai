using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.NimbleWay;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.NimbleWay;

public sealed class NimbleWayProviderEndpointTests
{
    [Fact]
    public async Task MessagesAndResponses_KeepProgressSeparateFromImmutableFinalToolOutput()
    {
        const string firstProgressJson = """{"type":"progress","message":"First progress","future":1}""";
        const string secondProgressJson = """{"type":"progress","message":"Second progress","future":2}""";
        const string finalOutputJson = """{"output":{"type":"text","content":"Final answer"},"final_field":true}""";
        var provider = new NimbleWayProvider(new KeyResolver(), new ClientFactory(new HttpClient(new Handler((http, _) =>
        {
            var path = http.RequestUri!.AbsolutePath;
            if (path.EndsWith("/events"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"data: {firstProgressJson}\n\ndata: {secondProgressJson}\n\n", Encoding.UTF8, "text/event-stream") });
            var json = path.EndsWith("/result")
                ? finalOutputJson
                : """{"id":"task_run_1","web_search_agent_id":"wsa_1","status":"completed"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }))));
        var responseRequest = JsonSerializer.Deserialize<ResponseRequest>("""{"model":"nimbleway/agent/wsa_1","input":"query"}""", JsonSerializerOptions.Web)!;
        var messageRequest = JsonSerializer.Deserialize<MessagesRequest>("""{"model":"nimbleway/agent/wsa_1","max_tokens":1000,"messages":[{"role":"user","content":"query"}]}""", JsonSerializerOptions.Web)!;
        var responses = await CollectImmutableSnapshots(provider.ResponsesStreamingAsync(responseRequest));
        var completedItems = responses
            .Where(part => part.GetProperty("type").GetString() == "response.output_item.done")
            .Select(part => part.GetProperty("item")).ToList();
        // Message items also serialize a null name; classify tools by their item type.
        var completedTools = completedItems
            .Where(item => item.GetProperty("type").GetString() == "custom_tool_call").ToList();
        Assert.Equal(3, completedTools.Count);
        Assert.Equal(3, completedTools.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(new[] { "agent_run_event", "agent_run_event", "agent_run" },
            completedTools.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(new[] { "task_run_1_event_0", "task_run_1_event_1", "task_run_1" },
            completedTools.Select(item => item.GetProperty("id").GetString()));
        var expectedOutputs = new[] { firstProgressJson, secondProgressJson, finalOutputJson }
            .Select(json => JsonSerializer.Deserialize<JsonElement>(json)).ToArray();
        for (var index = 0; index < expectedOutputs.Length; index++)
            Assert.True(JsonElement.DeepEquals(expectedOutputs[index], completedTools[index].GetProperty("output")));

        var finalRun = Assert.Single(completedTools, item => item.GetProperty("name").GetString() == "agent_run");
        Assert.True(finalRun.GetProperty("output").GetProperty("final_field").GetBoolean());
        var assistant = Assert.Single(completedItems, item => item.GetProperty("type").GetString() == "message");
        Assert.Equal(4, completedItems.Count);
        Assert.Equal("Final answer", assistant.GetProperty("content")[0].GetProperty("text").GetString());

        var completedResponse = Assert.Single(responses, part => part.GetProperty("type").GetString() == "response.completed");
        var finalTools = completedResponse.GetProperty("response").GetProperty("output").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "custom_tool_call").ToList();
        Assert.Equal(3, finalTools.Count);
        foreach (var tool in completedTools)
        {
            var finalTool = Assert.Single(finalTools, item => item.GetProperty("id").GetString() == tool.GetProperty("id").GetString());
            Assert.True(JsonElement.DeepEquals(tool, finalTool));
        }

        var messages = await CollectImmutableSnapshots(provider.MessagesStreamingAsync(messageRequest, []));
        var outputs = messages.Where(part => part.GetProperty("type").GetString() == "content_block_start")
            .Select(part => part.GetProperty("content_block"))
            .Where(block => block.GetProperty("type").GetString() == "tool_result").ToList();
        Assert.Equal(3, outputs.Count);
        Assert.Equal(3, outputs.Select(block => block.GetProperty("tool_use_id").GetString()).Distinct().Count());
        Assert.Contains("First progress", JsonSerializer.Serialize(outputs));
        Assert.Contains("Second progress", JsonSerializer.Serialize(outputs));
        Assert.Contains("final_field", JsonSerializer.Serialize(outputs));
        Assert.Equal(completedTools.Select(item => item.GetProperty("id").GetString()),
            outputs.Select(block => block.GetProperty("tool_use_id").GetString()));
        Assert.Equal(new[] { "agent_run_event", "agent_run_event", "agent_run" },
            outputs.Select(block => block.GetProperty("name").GetString()));
        for (var index = 0; index < expectedOutputs.Length; index++)
        {
            var content = JsonSerializer.Deserialize<JsonElement>(outputs[index].GetProperty("content").GetString()!);
            Assert.True(JsonElement.DeepEquals(expectedOutputs[index], content));
        }

        var textDelta = Assert.Single(messages, part => part.GetProperty("type").GetString() == "content_block_delta"
            && part.GetProperty("delta").GetProperty("type").GetString() == "text_delta");
        Assert.Equal("Final answer", textDelta.GetProperty("delta").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("search")]
    [InlineData("agent/wsa_1")]
    public async Task AllFourEndpoints_UseUnifiedExecutionAndRetainNativeMetadata(string model)
    {
        var calls = 0;
        var provider = new NimbleWayProvider(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(async (http, ct) =>
        {
            calls++;
            if (http.Method == HttpMethod.Post)
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("query", payload.GetProperty(model == "search" ? "query" : "input").GetString());
                Assert.True(payload.GetProperty("future_option").GetProperty("flag").GetBoolean());
            }
            var json = model == "search"
                ? """{"answer":"Unified answer","results":[{"url":"https://example.com","title":"Source"}],"future_field":{"flag":true}}"""
                : http.Method == HttpMethod.Post
                    ? """{"id":"task_run_1","web_search_agent_id":"wsa_1","interaction_id":"interaction-1","status":"completed"}"""
                    : """{"output":{"type":"text","content":"Unified answer","trust":{"sources":[{"url":"https://example.com","title":"Source"}]}},"future_field":{"flag":true}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }))));
        var chat = JsonSerializer.Deserialize<ChatCompletionOptions>($$$$"""
            {"model":"nimbleway/{{{{model}}}}","messages":[{"role":"user","content":"query"}],"providerMetadata":{"nimbleway":{"future_option":{"flag":true},"enable_events":false}}}
            """, JsonSerializerOptions.Web)!;
        var responses = JsonSerializer.Deserialize<ResponseRequest>($$$$"""
            {"model":"nimbleway/{{{{model}}}}","input":"query","metadata":{"nimbleway":{"future_option":{"flag":true},"enable_events":false}}}
            """, JsonSerializerOptions.Web)!;
        var messages = JsonSerializer.Deserialize<MessagesRequest>($$$$"""
            {"model":"nimbleway/{{{{model}}}}","max_tokens":1000,"messages":[{"role":"user","content":"query"}],"metadata":{"nimbleway":{"future_option":{"flag":true},"enable_events":false}}}
            """, JsonSerializerOptions.Web)!;
        var ui = new ChatRequest
        {
            Model = $"nimbleway/{model}",
            ProviderMetadata = new() { ["nimbleway"] = JsonSerializer.SerializeToElement(new { future_option = new { flag = true }, enable_events = false }) },
            Messages = [new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "query" }] }]
        };
        var chatResult = await provider.CompleteChatAsync(chat);
        var responseResult = await provider.ResponsesAsync(responses);
        var messageResult = await provider.MessagesAsync(messages, []);
        Assert.Contains("Unified answer", JsonSerializer.Serialize(chatResult));
        Assert.Contains("Unified answer", JsonSerializer.Serialize(responseResult));
        Assert.Contains("Unified answer", JsonSerializer.Serialize(messageResult));
        Assert.Contains("future_field", JsonSerializer.Serialize(chatResult));
        Assert.Contains("future_field", JsonSerializer.Serialize(responseResult));
        Assert.Contains("future_field", JsonSerializer.Serialize(messageResult));
        var output = JsonSerializer.SerializeToElement(responseResult.Output);
        Assert.Equal("message", Assert.Single(output.EnumerateArray()).GetProperty("type").GetString());

        var chatStream = await Collect(provider.CompleteChatStreamingAsync(chat));
        var responseStream = await Collect(provider.ResponsesStreamingAsync(responses));
        var messageStream = await Collect(provider.MessagesStreamingAsync(messages, []));
        var uiStream = await Collect(provider.StreamAsync(ui));
        Assert.Contains("Unified answer", JsonSerializer.Serialize(chatStream));
        Assert.Contains("Unified answer", JsonSerializer.Serialize(responseStream));
        Assert.Contains("Unified answer", JsonSerializer.Serialize(messageStream));
        Assert.Contains("Unified answer", JsonSerializer.Serialize(uiStream));
        Assert.Contains("future_field", JsonSerializer.Serialize(chatStream));
        Assert.Contains("future_field", JsonSerializer.Serialize(responseStream));
        Assert.Contains("future_field", JsonSerializer.Serialize(messageStream));
        Assert.Contains("future_field", JsonSerializer.Serialize(uiStream));
        Assert.Single(uiStream.OfType<SourceUIPart>());
        Assert.Equal(model == "search" ? 7 : 14, calls);
    }

    private static async Task<List<JsonElement>> CollectImmutableSnapshots<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        var snapshots = new List<JsonElement>();
        await foreach (var item in stream)
        {
            items.Add(item);
            snapshots.Add(JsonSerializer.SerializeToElement(item));
        }

        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(snapshots), JsonSerializer.SerializeToElement(items)),
            "Stream parts must not change after emission.");
        return snapshots;
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name = "") => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
