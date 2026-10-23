using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepSeek;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.DeepSeek;

public class DeepSeekProviderIntegrationTests
{
    private const string SearchResults = """
        {"results":[
          {"title":"First","url":"https://example.com/one","snippet":"First snippet","summary":"Long summary"},
          {"title":"Second","url":"https://example.com/two","snippet":"Second snippet"},
          {"title":"First again","url":"https://example.com/one","snippet":"Another snippet"}
        ]}
        """;

    [Fact]
    public async Task Unified_inference_uses_responses_preserves_history_and_normalizes_legacy_options()
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/responses", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-key", http.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("max", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.False(root.TryGetProperty("thinking", out _));
            Assert.False(root.TryGetProperty("reasoning_effort", out _));
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.Equal(3, root.GetProperty("input").GetArrayLength());
            Assert.Equal("system instruction", root.GetProperty("instructions").GetString());
            Assert.Equal("web_search", root.GetProperty("tools")[0].GetProperty("type").GetString());
            return Json(Response());
        });
        var request = Request("deepseek-v4-pro", "new question", new
        {
            thinking = new { type = "enabled" }, reasoning_effort = "max", tools = new[] { new { type = "web_search" } }
        });
        request = new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Metadata = request.Metadata,
            Instructions = "system instruction",
            Input = new AIInput { Items = [Message("user", "old question"), Message("assistant", "old answer"), Message("user", "new question")] }
        };
        var response = await provider.ExecuteUnifiedAsync(request);
        Assert.Contains(response.Output!.Items!, item => item.Type == "message");
        Assert.True(GatewayCost(response.Metadata!) > 0m);
        // Normalization must not mutate a saved caller-owned configuration.
        Assert.True(((JsonElement)request.Metadata!["deepseek"]!).TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData("{\"thinking\":{\"type\":\"disabled\"},\"reasoning_effort\":\"max\"}", "none")]
    [InlineData("{\"thinking\":{\"type\":\"disabled\"},\"reasoning\":{\"effort\":\"low\"}}", "low")]
    public async Task Explicit_reasoning_takes_precedence_and_disabled_reasoning_uses_none(string options, string expected)
    {
        var provider = Provider(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(expected, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            return Json(Response());
        });
        await provider.ExecuteUnifiedAsync(Request("deepseek-v4-pro", "question", JsonSerializer.Deserialize<JsonElement>(options)));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("incomplete")]
    public async Task Responses_stream_maps_reasoning_tools_sources_and_terminal_usage_without_search_surcharge(string status)
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/responses", http.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            const string citation = """{"type":"url_citation","url":"https://example.com","title":"Example","start_index":0,"end_index":6}""";
            var finalResponse = Response(status).Replace("\"annotations\":[]", $"\"annotations\":[{citation}]");
            using var finalDocument = JsonDocument.Parse(finalResponse);
            var message = finalDocument.RootElement.GetProperty("output")[0];
            return Sse(
                """{"type":"response.reasoning_text.delta","item_id":"r1","output_index":0,"content_index":0,"delta":"Reasoning"}""",
                """{"type":"response.reasoning_text.done","item_id":"r1","output_index":0,"content_index":0,"text":"Reasoning"}""",
                """{"type":"response.output_item.added","output_index":1,"item":{"id":"ws1","type":"web_search_call","status":"in_progress","action":{"type":"search","query":"question"}}}""",
                """{"type":"response.output_item.done","output_index":1,"item":{"id":"ws1","type":"web_search_call","status":"completed","action":{"type":"search","query":"question","sources":[{"type":"url","url":"https://example.com","title":"Example"}]}}}""",
                """{"type":"response.output_item.added","output_index":2,"item":{"id":"m1","type":"message","role":"assistant","status":"in_progress","content":[]}}""",
                """{"type":"response.content_part.added","item_id":"m1","output_index":2,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""",
                """{"type":"response.output_text.delta","item_id":"m1","output_index":2,"content_index":0,"delta":"Answer"}""",
                """{"type":"response.output_text.annotation.added","item_id":"m1","output_index":2,"content_index":0,"annotation_index":0,"annotation":{"type":"url_citation","url":"https://example.com","title":"Example","start_index":0,"end_index":6}}""",
                """{"type":"response.output_text.done","item_id":"m1","output_index":2,"content_index":0,"text":"Answer"}""",
                // URL citations are finalized with their completed output-text part.
                JsonSerializer.Serialize(new
                {
                    type = "response.content_part.done", item_id = "m1", output_index = 2, content_index = 0,
                    part = message.GetProperty("content")[0]
                }),
                JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 2, item = message }),
                "{\"type\":\"response." + status + "\",\"response\":" + finalResponse + "}");
        });
        var events = await Collect(provider.StreamUnifiedAsync(Request("deepseek-v4-pro", "question")));
        Assert.Contains(events, evt => evt.Event.Type == "reasoning-delta");
        Assert.Contains(events, evt => evt.Event.Type == "tool-input-available");
        Assert.Contains(events, evt => evt.Event.Type == "source-url");
        Assert.Contains(events, evt => evt.Event.Type == "text-delta");
        var source = Assert.IsType<AISourceUrlEventData>(Assert.Single(events.Where(evt => evt.Event.Type == "source-url")).Event.Data);
        Assert.Equal("https://example.com", source.Url);
        Assert.Equal("Example", source.Title);
        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(events.Where(evt => evt.Event.Type == "finish")).Event.Data);
        Assert.Equal(100, finish.InputTokens);
        Assert.Equal(20, finish.OutputTokens);
        var pricing = DeepSeekProvider.SelectRuntimePricingForTests("deepseek-v4-pro",
            "deepseek".GetPricing()!["deepseek/deepseek-v4-pro"], DateTimeOffset.UtcNow)!;
        Assert.Equal(100 * pricing.Input + 20 * pricing.Output, finish.MessageMetadata!.Gateway!.Cost);
    }

    [Fact]
    public async Task Failed_response_stream_emits_provider_error()
    {
        var provider = Provider((_, _) => Task.FromResult(Sse("""
            {"type":"response.failed","response":{"id":"r","object":"response","model":"deepseek-v4-pro","status":"failed","output":[],"error":{"code":"bad_request","message":"Provider failure"}}}
            """)));
        var events = await Collect(provider.StreamUnifiedAsync(Request("deepseek-v4-pro", "question")));
        Assert.Equal("Provider failure", Assert.IsType<AIErrorEventData>(Assert.Single(events.Where(e => e.Event.Type == "error")).Event.Data).ErrorText);
    }

    [Fact]
    public async Task Model_listing_adds_search_and_fetch_with_existing_pricing_shape()
    {
        var provider = Provider((http, _) =>
        {
            Assert.Equal("/models", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"data":[{"id":"deepseek-v4-pro","owned_by":"deepseek"}]}"""));
        });
        var models = (await provider.ListModels()).ToList();
        foreach (var id in new[] { "deepseek/search", "deepseek/fetch" })
        {
            var model = Assert.Single(models.Where(m => m.Id == id));
            Assert.Equal("language", model.Type);
            Assert.Equal(0.00075m, model.Pricing!.Input);
            Assert.Equal(0m, model.Pricing.Output);
            Assert.Contains("per successful non-empty request", model.Description);
            Assert.False(JsonSerializer.SerializeToElement(model.Pricing).TryGetProperty("request", out _));
        }
        Assert.Equal(3, models.Count);
    }

    [Fact]
    public async Task Search_uses_last_user_text_and_supported_options_returns_one_message_and_distinct_sources()
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/v1/tools/search", http.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("new question", root.GetProperty("query").GetString());
            Assert.Equal(50, root.GetProperty("count").GetInt32());
            Assert.True(root.GetProperty("need_content").GetBoolean());
            Assert.False(root.TryGetProperty("tools", out _));
            Assert.False(root.TryGetProperty("ori_question", out _));
            return Json(SearchResults);
        });
        var request = Request("deepseek/search", "new question", new { count = 50, need_content = true, ori_question = (string?)null, tools = new[] { new { type = "web_search" } } });
        var response = await provider.ExecuteUnifiedAsync(request);
        var text = Assert.IsType<AITextContentPart>(Assert.Single(response.Output!.Items!.Where(i => i.Type == "message")).Content!.Single()).Text;
        Assert.Contains("First\n\nhttps://example.com/one\n\nFirst snippet", text);
        Assert.Contains("Second snippet", text);
        Assert.Equal(2, response.Output.Items!.Count(i => i.Type == "source-url"));
        Assert.Equal(0.00075m, GatewayCost(response.Metadata!));
        Assert.False(JsonSerializer.SerializeToElement(response.Usage).TryGetProperty("input_tokens", out _));
    }

    [Theory]
    [InlineData("search", "{\"results\":[]}")]
    [InlineData("fetch", "{\"title\":\"Empty\",\"content\":\"\"}")]
    public async Task Empty_success_is_free_in_unified_and_ui_finish(string model, string json)
    {
        var provider = Provider((_, _) => Task.FromResult(Json(json)));
        var response = await provider.ExecuteUnifiedAsync(Request(model, "https://example.com"));
        Assert.Equal(0m, GatewayCost(response.Metadata!));
        var chat = Chat(model, "https://example.com");
        var parts = await Collect(provider.StreamAsync(chat));
        Assert.Equal(0m, Assert.IsType<FinishUIPart>(Assert.Single(parts.OfType<FinishUIPart>())).MessageMetadata!.Gateway!.Cost);
    }

    [Theory]
    [InlineData("search", SearchResults, "question")]
    [InlineData("fetch", "{\"title\":\"Page\",\"content\":\"# Page\\n\\nMarkdown body\"}", "Read [this page](https://example.com/page).")]
    public async Task Chat_stream_produces_text_sources_and_single_request_cost(string model, string json, string prompt)
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal($"/v1/tools/{model}", http.RequestUri!.AbsolutePath);
            if (model == "fetch")
            {
                using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("https://example.com/page", body.RootElement.GetProperty("url").GetString());
            }
            return Json(json);
        });
        var parts = await Collect(provider.StreamAsync(Chat(model, prompt)));
        Assert.Contains(parts, part => part.Type == "text-delta");
        Assert.Contains(parts, part => part.Type == "source-url");
        var finish = Assert.Single(parts.OfType<FinishUIPart>());
        Assert.Equal(0.00075m, finish.MessageMetadata!.Gateway!.Cost);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Search_length_is_measured_in_unicode_characters(int length, bool valid)
    {
        var calls = 0;
        var provider = Provider((_, _) => { calls++; return Task.FromResult(Json("{\"results\":[]}")); });
        var request = Request("search", string.Concat(Enumerable.Repeat("😀", length)));
        if (valid) await provider.ExecuteUnifiedAsync(request);
        else await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(request));
        Assert.Equal(valid ? 1 : 0, calls);
    }

    [Theory]
    [InlineData("search", " ", "{}")]
    [InlineData("search", "question", "{\"count\":0}")]
    [InlineData("search", "question", "{\"count\":51}")]
    [InlineData("search", "question", "{\"count\":1.5}")]
    [InlineData("search", "question", "{\"need_content\":\"true\"}")]
    [InlineData("search", "question", "{\"ori_question\":\" \"}")]
    [InlineData("search", "question", "{\"localization\":\"invalid\"}")]
    [InlineData("fetch", "No URL", "{}")]
    [InlineData("fetch", "https://example.com https://other.test", "{}")]
    [InlineData("fetch", "https://user:password@example.com", "{}")]
    [InlineData("fetch", "ftp://example.com", "{}")]
    public async Task Invalid_inputs_fail_before_http(string model, string input, string options)
    {
        var provider = Provider((_, _) => throw new Xunit.Sdk.XunitException("No HTTP request expected"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(model, input, JsonSerializer.Deserialize<JsonElement>(options))));
    }

    [Theory]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Upstream_errors_are_not_retried_and_preserve_status(int status)
    {
        var calls = 0;
        var provider = Provider((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"message\":\"Provider failure\"}}") });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request("search", "question")));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("Provider failure", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_is_preserved()
    {
        var provider = Provider(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request("search", "question"), cts.Token));
    }

    private static string Response(string status = "completed") => """
        {"id":"resp1","object":"response","created_at":1792660000,"status":"STATUS","model":"deepseek-v4-pro",
        "output":[{"id":"m1","type":"message","role":"assistant","content":[{"type":"output_text","text":"Answer","annotations":[]}]}],
        "usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120}}
        """.Replace("STATUS", status);

    private static AIRequest Request(string model, string prompt, object? options = null) => new()
    {
        ProviderId = "deepseek", Model = model,
        Input = new AIInput { Items = [Message("user", "old question"), Message("assistant", "old answer"), Message("user", prompt)] },
        Metadata = options is null ? null : new() { ["deepseek"] = JsonSerializer.SerializeToElement(options) }
    };

    private static AIInputItem Message(string role, string text) => new()
    {
        Type = "message", Role = role, Content = [new AITextContentPart { Type = "text", Text = text }]
    };

    private static ChatRequest Chat(string model, string prompt) => JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(new
    {
        id = "chat", model,
        messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = prompt } } } }
    }), JsonSerializerOptions.Web)!;

    private static decimal GatewayCost(Dictionary<string, object?> metadata)
        => JsonSerializer.SerializeToElement(metadata).GetProperty("gateway").GetProperty("cost").GetDecimal();

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(params string[] events)
    {
        var frames = events.Select(evt =>
        {
            using var document = JsonDocument.Parse(evt);
            // Every physical line of an SSE payload must have its own data prefix.
            return $"data: {evt.ReplaceLineEndings("\n").Replace("\n", "\ndata: ")}\n\n";
        });
        return new(HttpStatusCode.OK)
        { Content = new StringContent(string.Concat(frames), Encoding.UTF8, "text/event-stream") };
    }

    private static DeepSeekProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new Keys(), new Factory(handler), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(new Handler(handler)); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken); }
}
