using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.NimbleWay;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.NimbleWay;

public sealed class NimbleWayProviderUnifiedTests
{
    private const string SearchResult = """
        {"request_id":"search-1","answer":"Answer","total_results":1,"results":[{"title":"Page","url":"https://example.com","description":"Snippet","content":"Full content","extra_fields":{"custom":[1,true]}}],"future_field":{"nested":true}}
        """;
    private const string Result = """
        {"output":{"type":"text","content":"Research answer","trust":{"sources":[{"title":"Source","url":"https://example.com","extra":[1,true]}],"claims":[{"citations":[{"url":"https://example.com"},{"title":"Citation","url":"https://example.org","excerpts":["evidence"]}],"confidence":"high"}]}},"run":{"id":"task_run_1","status":"completed"},"future_field":{"nested":true}}
        """;

    [Fact]
    public async Task Search_UsesUnifiedInputAndPreservesRawOptionsAndResults()
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/v2/search", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            var payload = await Payload(http, ct);
            Assert.Equal("Instructions\n\nquery", payload.GetProperty("query").GetString());
            Assert.True(payload.GetProperty("custom").GetProperty("nested")[0].GetBoolean());
            Assert.Equal(4, payload.GetProperty("max_results").GetInt32());
            return Json(SearchResult);
        });
        Assert.IsAssignableFrom<IUnifiedModelProvider>(provider);
        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nimbleway", Model = "nimbleway/search", Instructions = "Instructions",
            Input = new AIInput { Text = "query" },
            Metadata = new() { ["nimbleway"] = new { query = "ignored", max_results = 4, custom = new { nested = new object[] { true, 2 } } } }
        });
        Assert.Null(response.Usage);
        var text = Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text;
        Assert.Contains("Answer", text);
        Assert.Contains("Full content", text);
        Assert.Contains("URL: https://example.com", text);
        var source = Assert.Single(response.Output.Items, item => item.Type == "source-url");
        var sourceRaw = (JsonElement)source.Metadata!["nimbleway"]!;
        Assert.True(sourceRaw.GetProperty("extra_fields").GetProperty("custom")[1].GetBoolean());
        Assert.True(((JsonElement)response.Metadata!["nimbleway"]!).GetProperty("future_field").GetProperty("nested").GetBoolean());
    }

    [Theory]
    [InlineData("chatcompletions.request.metadata")]
    [InlineData("chatcompletions.request.provider_metadata")]
    [InlineData("messages.request.metadata")]
    [InlineData("messages.request.provider_metadata")]
    public async Task ProtocolMetadata_PassesThroughWithoutTypedProviderOptions(string key)
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.True((await Payload(http, ct)).GetProperty("unknown").GetProperty("flag").GetBoolean());
            return Json(SearchResult);
        });
        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nimbleway", Model = "search", Input = new AIInput { Text = "query" },
            Metadata = new() { [key] = JsonSerializer.SerializeToElement(new { nimbleway = new { unknown = new { flag = true } } }) }
        });
    }

    [Fact]
    public async Task Agent_PollsV2RunThenFetchesResultAndPreservesTrustAndNativeFields()
    {
        var paths = new List<string>();
        var provider = Provider(async (http, ct) =>
        {
            paths.Add(http.RequestUri!.AbsolutePath);
            if (http.Method == HttpMethod.Post)
            {
                Assert.Equal("/v2/agents/wsa_1/runs", http.RequestUri.AbsolutePath);
                var payload = await Payload(http, ct);
                Assert.Equal("query", payload.GetProperty("input").GetString());
                Assert.Equal("x-high", payload.GetProperty("effort").GetString());
                Assert.False(payload.GetProperty("enable_events").GetBoolean());
                Assert.True(payload.GetProperty("input_data").GetProperty("flag").GetBoolean());
                Assert.True(payload.GetProperty("output_schema").GetProperty("properties").TryGetProperty("name", out _));
                return Json(Run("queued"));
            }
            return Json(http.RequestUri.AbsolutePath.EndsWith("/result") ? Result : Run("completed"));
        });
        var response = await provider.ExecuteUnifiedAsync(Request("agent/wsa_1", new
        {
            effort = "x-high", input = "ignored", input_data = new { flag = true },
            output_schema = new { type = "object", properties = new { name = new { type = "string" } } }
        }));
        Assert.Equal(new[] { "/v2/agents/wsa_1/runs", "/v2/agents/wsa_1/runs/task_run_1", "/v2/agents/wsa_1/runs/task_run_1/result" }, paths);
        Assert.Equal("Research answer", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(2, response.Output.Items.Count(item => item.Type == "source-url"));
        var raw = (JsonElement)response.Metadata!["nimbleway"]!;
        Assert.Equal("interaction-1", raw.GetProperty("create").GetProperty("interaction_id").GetString());
        Assert.Equal("completed", raw.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal("high", raw.GetProperty("result").GetProperty("output").GetProperty("trust").GetProperty("claims")[0].GetProperty("confidence").GetString());
        Assert.True(raw.GetProperty("result").GetProperty("future_field").GetProperty("nested").GetBoolean());
    }

    [Fact]
    public async Task AgentName_UsesNamedRouteAndMapsStructuredOutputWithoutChangingSchema()
    {
        var provider = Provider(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                Assert.Equal("/v2/agents/runs", http.RequestUri!.AbsolutePath);
                var payload = await Payload(http, ct);
                Assert.Equal("company-research", payload.GetProperty("agent_name").GetString());
                Assert.Equal("query", payload.GetProperty("input").GetString());
                Assert.Equal("string", payload.GetProperty("output_schema").GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
                return Json(Run("completed"));
            }
            return Json("""{"output":{"type":"json","content":{"name":"Company","future":[1,true]}},"run":{"status":"completed"}}""");
        });
        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nimbleway", Model = "agent/company-research", Input = new AIInput { Text = "query" },
            ResponseFormat = new { type = "json_schema", json_schema = new { name = "record", schema = new { type = "object", properties = new { name = new { type = "string" } } } } }
        });
        using var parsed = JsonDocument.Parse(Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.True(parsed.RootElement.GetProperty("future")[1].GetBoolean());
    }

    [Fact]
    public async Task Stream_EmitsLiveRawProgressAndFetchesAuthoritativeResultAfterSseCloses()
    {
        var paths = new List<string>();
        var provider = Provider(async (http, ct) =>
        {
            paths.Add(http.RequestUri!.AbsolutePath);
            if (http.Method == HttpMethod.Post)
            {
                Assert.True((await Payload(http, ct)).GetProperty("enable_events").GetBoolean());
                return Json(Run("queued"));
            }
            if (http.RequestUri.AbsolutePath.EndsWith("/events"))
            {
                Assert.Contains(http.Headers.Accept, header => header.MediaType == "text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(": keep-alive\n\nevent: progress\ndata: {\"type\":\"progress\",\ndata: \"message\":\"Searching\",\"future\":[true,2]}\n\n: keep-alive\n\ndata: {\"type\":\"new_event\",\"nested\":{\"flag\":true}}", Encoding.UTF8, "text/event-stream")
                };
            }
            return Json(http.RequestUri.AbsolutePath.EndsWith("/result") ? Result : Run("completed"));
        });
        var events = await Collect(provider.StreamUnifiedAsync(Request("agent/wsa_1")));
        var preliminary = events.Select(evt => evt.Event.Data).OfType<AIToolOutputAvailableEventData>().Where(data => data.Preliminary == true).ToList();
        Assert.Equal(2, preliminary.Count);
        Assert.Equal("Searching", ((JsonElement)preliminary[0].Output).GetProperty("message").GetString());
        Assert.True(preliminary[0].ProviderMetadata!["nimbleway"]["future"] is JsonElement);
        Assert.True(((JsonElement)preliminary[1].Output).GetProperty("nested").GetProperty("flag").GetBoolean());
        Assert.Single(events, evt => evt.Event.Type == "text-start");
        Assert.NotEqual(events.Single(evt => evt.Event.Type == "tool-input-start").Event.Id,
            events.Single(evt => evt.Event.Type == "text-start").Event.Id);
        Assert.Equal("Research answer", Assert.Single(events.Select(evt => evt.Event.Data).OfType<AITextDeltaEventData>()).Delta);
        Assert.Equal(2, events.Count(evt => evt.Event.Type == "source-url"));
        Assert.All(events.Where(evt => evt.Event.Type == "source-url"),
            source => Assert.Equal(events.Single(evt => evt.Event.Type == "text-start").Event.Id, source.Event.Id));
        Assert.Single(events, evt => evt.Event.Type == "finish");
        Assert.Equal("finish", events.Last().Event.Type);
        Assert.Equal(4, paths.Count);
        Assert.Single(events.Select(evt => evt.Metadata!["chatcompletions.stream.id"]).Distinct());
        Assert.Contains("future_field", JsonSerializer.Serialize(((AIFinishEventData)events.Last().Event.Data!).MessageMetadata));
    }

    [Fact]
    public async Task Stream_ExplicitEventOptOutUsesPolling()
    {
        var provider = Provider((http, _) =>
        {
            Assert.DoesNotContain("/events", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(http.Method == HttpMethod.Post ? Run("completed") : Result));
        });
        var events = await Collect(provider.StreamUnifiedAsync(Request("agent/wsa_1", new { enable_events = false })));
        Assert.Single(events, evt => evt.Event.Type == "finish");
    }

    [Fact]
    public async Task Stream_ProgressIsYieldedBeforeFetchingFinalOutput()
    {
        var provider = Provider((http, _) =>
        {
            Assert.DoesNotContain("/result", http.RequestUri!.AbsolutePath);
            if (http.Method == HttpMethod.Get)
                Assert.EndsWith("/events", http.RequestUri.AbsolutePath);
            return Task.FromResult(http.Method == HttpMethod.Post
                ? Json(Run("queued"))
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("data: {\"type\":\"progress\",\"message\":\"Live progress\"}\n\n", Encoding.UTF8, "text/event-stream") });
        });
        await using var stream = provider.StreamUnifiedAsync(Request("agent/wsa_1")).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("tool-input-start", stream.Current.Event.Type);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("tool-input-available", stream.Current.Event.Type);
        Assert.True(await stream.MoveNextAsync());
        var output = Assert.IsType<AIToolOutputAvailableEventData>(stream.Current.Event.Data);
        Assert.True(output.Preliminary);
        Assert.Equal("Live progress", ((JsonElement)output.Output).GetProperty("message").GetString());
    }

    [Fact]
    public async Task CancellationDuringSse_PropagatesWithoutFetchingFinalOutput()
    {
        using var cts = new CancellationTokenSource();
        var provider = Provider((http, _) =>
        {
            if (http.Method == HttpMethod.Post)
                return Task.FromResult(Json(Run("queued")));
            Assert.EndsWith("/events", http.RequestUri!.AbsolutePath);
            cts.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("data: {\"type\":\"progress\"}\n\n", Encoding.UTF8, "text/event-stream") });
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider.StreamUnifiedAsync(Request("agent/wsa_1"), cts.Token)));
    }

    [Fact]
    public async Task Conversation_PreservesInstructionsAndHistoryAcrossUnifiedInputs()
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("Instructions\n\nsystem: Rules\n\nuser: First\n\nassistant: Reply\n\nuser: Follow-up",
                (await Payload(http, ct)).GetProperty("query").GetString());
            return Json(SearchResult);
        });
        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "nimbleway", Model = "search", Instructions = "Instructions",
            Input = new AIInput
            {
                Items =
                [
                    new() { Role = "system", Content = [new AITextContentPart { Type = "text", Text = "Rules" }] },
                    new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "First" }] },
                    new() { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "Reply" }] },
                    new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Follow-up" }] }
                ]
            }
        });
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("unknown")]
    public async Task FailedRuns_NeverFetchResultOrEmitFinish(string status)
    {
        var provider = Provider((http, _) =>
        {
            Assert.DoesNotContain("/result", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Run(status)));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request("agent/wsa_1")));
        var events = new List<AIStreamEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var evt in provider.StreamUnifiedAsync(Request("agent/wsa_1", new { enable_events = false })))
                events.Add(evt);
        });
        Assert.DoesNotContain(events, evt => evt.Event.Type == "finish");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"output\":null}")]
    public async Task MalformedResults_AreNotSuccessShaped(string json)
    {
        var provider = Provider((_, _) => Task.FromResult(Json(json)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request("search")));
    }

    [Fact]
    public async Task HttpFailure_PreservesStatusAndBody()
    {
        var provider = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("""{"detail":"Rate limited"}""") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request("search")));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("Rate limited", error.Message);
    }

    [Fact]
    public async Task CancellationDuringPolling_StopsWithoutFetchingOutput()
    {
        using var cts = new CancellationTokenSource();
        var provider = Provider((http, _) =>
        {
            if (http.Method != HttpMethod.Post)
                cts.Cancel();
            return Task.FromResult(Json(Run("running")));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request("agent/wsa_1"), cts.Token));
    }

    [Fact]
    public async Task InvalidInputAndOptions_DoNotSendHttp()
    {
        var provider = Provider((_, _) => throw new InvalidOperationException("Must not send HTTP"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(Request("unknown")));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(Request("agent/")));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request("search", new[] { 1, 2 })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(new AIRequest { ProviderId = "nimbleway", Model = "search" }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request("search"), cts.Token));
    }

    [Fact]
    public async Task Discovery_UsesV2EnvelopeMaximumPageSizeAndStableAgentIds()
    {
        var offsets = new List<string>();
        var provider = Provider((http, _) =>
        {
            Assert.Equal("/v2/agents", http.RequestUri!.AbsolutePath);
            offsets.Add(http.RequestUri.Query);
            return Task.FromResult(Json(offsets.Count == 1
                ? """{"items":[{"id":"wsa_1","agent_name":null,"display_name":"First","description":"Description","is_active":true},{"id":"wsa_inactive","display_name":"Inactive","is_active":false}],"total":3,"limit":200,"offset":0}"""
                : """{"items":[{"id":"wsa_2","agent_name":"named","display_name":"Second","is_active":true}],"total":3,"limit":200,"offset":2}"""));
        });
        var models = (await provider.ListModels()).ToList();
        Assert.Contains(models, model => model.Id == "nimbleway/agent/wsa_1" && model.Name == "First");
        Assert.Contains(models, model => model.Id == "nimbleway/agent/wsa_2");
        Assert.DoesNotContain(models, model => model.Id == "nimbleway/agent/wsa_inactive");
        Assert.Equal(new[] { "?limit=200&offset=0", "?limit=200&offset=2" }, offsets);
    }

    private static AIRequest Request(string model, object? options = null) => new()
    {
        ProviderId = "nimbleway", Model = model, Input = new AIInput { Text = "query" },
        Metadata = options is null ? null : new() { ["nimbleway"] = options }
    };
    private static string Run(string status)
        => JsonSerializer.Serialize(new { id = "task_run_1", web_search_agent_id = "wsa_1", interaction_id = "interaction-1", status, future_run = new { flag = true } });
    private static async Task<JsonElement> Payload(HttpRequestMessage request, CancellationToken ct)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }
    private static NimbleWayProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))));
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name = "") => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
