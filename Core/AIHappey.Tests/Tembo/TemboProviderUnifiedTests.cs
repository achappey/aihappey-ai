using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Tembo;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Tembo;

public class TemboProviderUnifiedTests
{
    private const string SessionId = "11111111-1111-4111-8111-111111111111";
    private const string AgentId = "22222222-2222-4222-8222-222222222222";
    private const string JobId = "33333333-3333-4333-8333-333333333333";
    private const string DirectModel = "tembo/claudeCode:claude-opus-5-5";

    [Fact]
    public async Task Discovery_UsesPaginatedAvailableRuntimeReferencesAndAgents_AndCachesByCredential()
    {
        var calls = new List<string>();
        var keys = new Keys();
        var provider = Provider(request =>
        {
            Assert.Equal(keys.Key, request.Headers.Authorization?.Parameter);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var url = request.RequestUri!.PathAndQuery;
            calls.Add(url);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/agents" => Json(url.Contains("cursor=")
                    ? Page($$"""{"id":"{{AgentId}}","name":"Duplicate"}""")
                    : Page($$"""{"id":"{{AgentId}}","name":"My configured agent","archivedAt":null}""", "a b")),
                "/v1/models" => Json(Page("""{"name":"claude-opus-5-5","enabled":true,"available":true},{"name":"disabled","enabled":false,"available":true}""")),
                "/v1/runtimes" => Json(Page("""
                    {"name":"claudeCode","displayName":"Claude Code","availability":{"status":"available"},
                    "compatibleModels":[{"name":"claude-opus-5-5","label":"Opus","provider":"Anthropic","availability":{"status":"available"}},
                    {"name":"disabled","label":"Disabled","availability":{"status":"available"}},
                    {"name":"missing","availability":{"status":"unavailable"}}]},
                    {"name":"codex","availability":{"status":"unavailable"},"compatibleModels":[]}
                    """)),
                _ => throw new Exception(url)
            };
        }, keys);
        var models = (await provider.ListModels()).ToList();
        Assert.Equal(2, models.Count);
        Assert.Contains(models, model => model.Id == $"tembo/agents/{AgentId}");
        Assert.Contains(models, model => model.Id == DirectModel);
        Assert.Contains(calls, url => url.Contains("cursor=a%20b"));
        var count = calls.Count;
        await provider.ListModels();
        Assert.Equal(count, calls.Count);
        keys.Key = "another-key";
        await provider.ListModels();
        Assert.Equal(count * 2, calls.Count);
    }

    [Fact]
    public async Task Discovery_RejectsRepeatedCursor_AndMissingKeyDoesNotSend()
    {
        var count = 0;
        var provider = Provider(_ => { count++; return Json(Page("", "repeat")); });
        Assert.Contains("repeated", (await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels())).Message);
        Assert.Equal(2, count);
        var keys = new Keys { Key = " " };
        Assert.Empty(await Provider(_ => throw new Exception("No request expected"), keys).ListModels());
    }

    [Fact]
    public async Task DirectSession_UsesDescriptionAndExactId_OnlyFinishesOnState_AndReturnsAssistantMessages()
    {
        var gets = 0;
        var eventCalls = 0;
        var artifactTypes = new HashSet<string>();
        JsonElement submitted = default;
        var provider = Provider(request =>
        {
            Assert.DoesNotContain("Postman", request.Headers.ToString());
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/sessions", request.RequestUri!.AbsolutePath);
                submitted = Body(request);
                return Json(Session("inProgress", pr: true));
            }
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/v1/sessions/{SessionId}") { gets++; return Json(Session(gets == 1 ? "inProgress" : "completed", pr: true)); }
            if (path.EndsWith("/events"))
            {
                eventCalls++;
                return Json(Page($$$$"""{"id":1,"sessionId":"{{{{SessionId}}}}","provider":"tembo","eventType":"unknown","eventData":{"type":"snapshot","accumulatedText":"Never guessed as a delta","scope":{"kind":"background"}}}"""));
            }
            if (path == "/v1/messages") return Json(Page(Message("new", "assistant", "Answer", "2026-10-05T01:00:01Z") + "," + Message("user", "user", "Secret input") + "," + Message("old", "assistant", "First", "2026-10-05T01:00:00Z")));
            if (path == "/v1/artifacts")
            {
                artifactTypes.Add(request.RequestUri.Query.Split("types=")[1].Split('&')[0]);
                return Json(Page());
            }
            throw new Exception(path);
        });
        var events = await Collect(provider.StreamUnifiedAsync(Request(options: new() { ["branchName"] = "feature", ["agentOptions"] = new { reasoningLevel = "high" } })));
        Assert.Equal("Do the work", submitted.GetProperty("description").GetString());
        Assert.Equal("claudeCode:claude-opus-5-5", submitted.GetProperty("agent").GetString());
        Assert.Equal("feature", submitted.GetProperty("branchName").GetString());
        Assert.False(submitted.TryGetProperty("pollIntervalSeconds", out _));
        Assert.False(submitted.TryGetProperty("prompt", out _));
        Assert.Equal(2, gets);
        Assert.Equal(3, eventCalls);
        Assert.Equal(7, artifactTypes.Count);
        Assert.Single(events, item => item.Event.Type == "data-tembo-session-event");
        var texts = events.Select(item => item.Event.Data).OfType<AITextDeltaEventData>().Select(data => data.Delta).ToList();
        Assert.Equal("First", texts[0]);
        Assert.Equal("Answer", texts[1]);
        Assert.Contains("Pull request", texts[2]);
        Assert.DoesNotContain(texts, text => text.Contains("Secret") || text.Contains("Never guessed"));
        var finish = Assert.Single(events, item => item.Event.Type == "finish");
        Assert.Equal("completed", finish.Metadata!["tembo.status"]);
        Assert.Equal(texts, finish.Event.Output!.Items!.Where(item => item.Role == "assistant")
            .SelectMany(item => item.Content!).OfType<AITextContentPart>().Select(part => part.Text));
    }

    [Fact]
    public async Task ExplicitPayload_PreservesNestedJsonAndNulls_WithoutDefaultsOrWrapper()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"description":"native","custom":{"list":[1,true,null,{"x":"value"}]},"queueRightAway":false}""");
        var calls = 0;
        var provider = Provider(request =>
        {
            calls++;
            Assert.Equal(payload.GetRawText(), Body(request).GetRawText());
            return Json(Session("queued"));
        });
        var response = await provider.ExecuteUnifiedAsync(Request(options: new() { ["payload"] = payload, ["waitForCompletion"] = false }));
        Assert.Equal(1, calls);
        Assert.Equal("queued", response.Status);
        Assert.Contains("not requested", Text(response));
    }

    [Fact]
    public async Task AgentQueue_PostsRawEventJson_AndNeverAssumesJobIsRunId()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"url":"https://example.com/issue","nested":{"nullable":null},"pollTimeoutSeconds":42}""");
        var count = 0;
        var provider = Provider(request =>
        {
            count++;
            Assert.Equal($"/v1/agents/{AgentId}/runs", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(payload.GetRawText(), Body(request).GetRawText());
            return Json($$"""{"jobId":"{{JobId}}","agentId":"{{AgentId}}","status":"queued"}""");
        });
        var response = await provider.ExecuteUnifiedAsync(Request($"tembo/agents/{AgentId}", new() { ["payload"] = payload, ["waitForCompletion"] = false }));
        Assert.Equal(1, count);
        Assert.Equal("queued", response.Status);
        Assert.Equal(JobId, response.Metadata!["tembo.job_id"]);
        Assert.Null(response.Metadata["tembo.session_id"]);
        Assert.Contains("not a completed result", Text(response));
    }

    [Fact]
    public async Task AgentAwaiting_ThrowsBeforeSubmitting_WhenRunIdentityContractIsUnknown()
    {
        var provider = Provider(_ => throw new Exception("Must not submit"));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(Request($"tembo/agents/{AgentId}")));
        Assert.Contains("jobId", error.Message);
        Assert.Contains("No run has been submitted", error.Message);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("tembo/agent")]
    [InlineData("claude-opus-5-5")]
    [InlineData("agents/not-a-uuid")]
    public async Task InvalidRoutes_FailBeforePost(string model)
        => await Assert.ThrowsAsync<ArgumentException>(() => Provider(_ => throw new Exception("Must not submit")).ExecuteUnifiedAsync(Request(model)));

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task FailedSession_DoesNotPretendPullRequestMeansSuccess(string state)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(request => Json(request.Method == HttpMethod.Post ? Session(state, true) : Page()))
            .ExecuteUnifiedAsync(Request()));

    [Fact]
    public async Task StateFlags_WorkWithoutOptionalCurrent_AndUnknownStateThrows()
    {
        var provider = Provider(request => Json(request.Method == HttpMethod.Post
            ? Session("completed").Replace("\"current\":\"completed\",", "") : Page()));
        Assert.Equal("completed", (await provider.ExecuteUnifiedAsync(Request())).Status);
        provider = Provider(_ => Json(Session("unsupported")));
        Assert.Contains("Unsupported", (await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(Request()))).Message);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellation_AreDistinct_AndPostIsNotRepeated()
    {
        var posts = 0;
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Json(request.RequestUri!.AbsolutePath.EndsWith("events") ? Page() : Session("inProgress"));
        });
        await Assert.ThrowsAsync<TimeoutException>(() => provider.ExecuteUnifiedAsync(Request(options: new() { ["pollTimeoutSeconds"] = 0.1 })));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request(), cts.Token));
        Assert.Equal(2, posts);
    }

    [Fact]
    public async Task HttpErrors_PreserveStatus_DoNotLeakPayload_AndPostIsNotRetried()
    {
        var count = 0;
        var provider = Provider(_ => { count++; return new(HttpStatusCode.InternalServerError) { Content = new StringContent("sensitive upstream body") }; });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OutputPagination_DrainsEveryPage_DeduplicatesMessages_AndPreservesArtifactLinks()
    {
        var keys = new Keys();
        var provider = Provider(request =>
        {
            Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            if (request.Method == HttpMethod.Post) { keys.Key = "changed-during-execution"; return Json(Session("completed")); }
            var second = request.RequestUri!.Query.Contains("cursor=");
            if (request.RequestUri.AbsolutePath.EndsWith("/events"))
                return Json(Page($$$$"""{"id":{{{{(second ? 2 : 1)}}}},"sessionId":"{{{{SessionId}}}}","eventData":{"type":"unknown"}}""", second ? null : "1"));
            if (request.RequestUri.AbsolutePath == "/v1/messages")
                return Json(second ? Page(Message("one", "assistant", "One") + "," + Message("two", "assistant", "Two", "2026-10-05T02:00:00Z"))
                    : Page(Message("one", "assistant", "One"), "next"));
            if (request.RequestUri.Query.Contains("types=Service"))
                return Json(Page($$"""{"id":"service","sessionId":"{{SessionId}}","title":"Preview","serviceUrl":"https://example.com/preview"}"""));
            return Json(Page());
        }, keys);
        var response = await provider.ExecuteUnifiedAsync(Request());
        Assert.Equal(2, Assert.IsType<List<JsonElement>>(response.Metadata!["tembo.events.raw"]).Count);
        var texts = response.Output!.Items!.Where(item => item.Role == "assistant").SelectMany(item => item.Content!).OfType<AITextContentPart>().ToList();
        Assert.Equal(3, texts.Count);
        Assert.Equal("One", texts[0].Text);
        Assert.Equal("Two", texts[1].Text);
        Assert.Contains("https://example.com/preview", texts[2].Text);
    }

    [Fact]
    public async Task TransientGetFailures_RetryBoundedly_WithCapturedCredential()
    {
        var gets = 0;
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(Session("completed"));
            if (request.RequestUri!.AbsolutePath.EndsWith("/events") && ++gets <= 2)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(TimeSpan.Zero) } };
            return Json(Page());
        });
        Assert.Equal("completed", (await provider.ExecuteUnifiedAsync(Request())).Status);
        Assert.Equal(3, gets);
    }

    [Fact]
    public async Task Guardrails_RejectUnqueuedAwaiting_InvalidPayload_AndCrossSessionMessages()
    {
        var provider = Provider(_ => throw new Exception("Must not submit"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(options: new() { ["queueRightAway"] = false })));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(options: new() { ["payload"] = new { agent = "native" } })));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(options: new() { ["pollTimeoutSeconds"] = -1 })));
        provider = Provider(request => Json(request.Method == HttpMethod.Post ? Session("completed")
            : request.RequestUri!.AbsolutePath == "/v1/messages" ? Page(Message("other", "assistant", "wrong").Replace(SessionId, AgentId)) : Page()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllFourConversationalAdapters_PreserveNativeMetadataAndOutput(bool agent)
    {
        var model = agent ? $"tembo/agents/{AgentId}" : DirectModel;
        var options = agent ? "\"waitForCompletion\":false,\"custom\":{\"x\":null}" : "\"custom\":{\"x\":null}";
        var posts = 0;
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal(JsonValueKind.Null, Body(request).GetProperty("custom").GetProperty("x").ValueKind);
                return Json(agent ? $$"""{"jobId":"{{JobId}}","agentId":"{{AgentId}}","status":"queued"}""" : Session("completed"));
            }
            return Json(request.RequestUri!.AbsolutePath == "/v1/messages" ? Page(Message("answer", "assistant", "Canonical answer")) : Page());
        });
        var chat = Deserialize<ChatCompletionOptions>($$$$"""{"model":"{{{{model}}}}","messages":[{"role":"user","content":"work"}],"metadata":{"tembo":{ {{{{options}}}} }}}""");
        var responses = Deserialize<ResponseRequest>($$$$"""{"model":"{{{{model}}}}","input":"work","metadata":{"tembo":{ {{{{options}}}} }}}""");
        var messages = Deserialize<MessagesRequest>($$$$"""{"model":"{{{{model}}}}","max_tokens":1000,"messages":[{"role":"user","content":"work"}],"metadata":{"tembo":{ {{{{options}}}} }}}""");
        var ui = Deserialize<ChatRequest>($$$$"""{"id":"chat","model":"{{{{model}}}}","messages":[{"id":"user","role":"user","parts":[{"type":"text","text":"work"}]}],"providerMetadata":{"tembo":{ {{{{options}}}} }}}""");
        var expected = agent ? "queue acknowledgement" : "Canonical answer";
        Assert.Contains(expected, JsonSerializer.Serialize(await provider.CompleteChatAsync(chat), JsonSerializerOptions.Web));
        Assert.Contains(expected, JsonSerializer.Serialize(await provider.ResponsesAsync(responses), JsonSerializerOptions.Web));
        Assert.Contains(expected, JsonSerializer.Serialize(await provider.MessagesAsync(messages, []), JsonSerializerOptions.Web));
        var chatStream = await Collect(provider.CompleteChatStreamingAsync(chat));
        var responseStream = await Collect(provider.ResponsesStreamingAsync(responses));
        var messageStream = await Collect(provider.MessagesStreamingAsync(messages, []));
        var uiStream = await Collect(provider.StreamAsync(ui));
        Assert.Contains(expected, JsonSerializer.Serialize(chatStream, JsonSerializerOptions.Web));
        Assert.Contains(expected, JsonSerializer.Serialize(responseStream, JsonSerializerOptions.Web));
        Assert.Contains(expected, JsonSerializer.Serialize(messageStream, JsonSerializerOptions.Web));
        Assert.Contains(expected, JsonSerializer.Serialize(uiStream, JsonSerializerOptions.Web));
        Assert.Contains("finish", JsonSerializer.Serialize(uiStream, JsonSerializerOptions.Web));
        Assert.Contains("message_stop", JsonSerializer.Serialize(messageStream, JsonSerializerOptions.Web));
        Assert.Equal(7, posts);
    }

    private static AIRequest Request(string model = DirectModel, Dictionary<string, object?>? options = null)
    {
        options ??= [];
        options.TryAdd("pollIntervalSeconds", 0.05);
        return new AIRequest { ProviderId = "tembo", Model = model, Input = new AIInput { Text = "Do the work" }, Metadata = new() { ["tembo"] = options } };
    }
    private static string Text(AIResponse response) => string.Join("\n", response.Output!.Items!
        .SelectMany(item => item.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text));
    private static JsonElement Body(HttpRequestMessage request) => JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;
    private static string Page(string items = "", string? cursor = null) => $$"""{"items":[{{items}}],"nextCursor":{{JsonSerializer.Serialize(cursor)}}}""";
    private static string Message(string id, string role, string content, string created = "2026-10-05T00:00:00Z")
        => $$"""{"id":"{{id}}","sessionId":"{{SessionId}}","role":"{{role}}","content":"{{content}}","createdAt":"{{created}}"}""";
    private static string Session(string state, bool pr = false)
        => JsonSerializer.Serialize(new
        {
            id = SessionId, title = "Task", artifacts = pr ? new[] { new { pullRequests = new[] { new { url = "https://example.com/pr/1", title = "Pull request" } } } } : [],
            state = new { current = state, isQueued = state == "queued", inProgress = state == "inProgress", isCompleted = state == "completed", isFailed = state == "failed", isCancelled = state == "cancelled" }
        });
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source) result.Add(item);
        return result;
    }
    private static TemboProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> responder, Keys? keys = null)
        => new(keys ?? new Keys(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Factory(responder));
    private sealed class Keys : IApiKeyResolver { public string Key { get; set; } = "test-key"; public string? Resolve(string provider) => Key; }
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
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
}
