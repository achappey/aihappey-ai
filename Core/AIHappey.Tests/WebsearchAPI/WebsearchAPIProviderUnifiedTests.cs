using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.WebsearchAPI;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.WebsearchAPI;

public sealed class WebsearchAPIProviderUnifiedTests
{
    private const string SearchJson = """
        {"answer":"Grounded answer","organic":[{"title":"Page","url":"https://example.com/final","description":"Snippet","content":"# Extracted content","position":1,"score":0.9,"futureSourceField":{"a":1}}],"responseTime":1.24,"futureField":{"nested":[true,2]}}
        """;
    private const string ScrapeJson = """
        {"code":200,"data":{"title":"Page [title]","url":"https://example.com/final","content":"# Extracted content","links":{"https://example.com/link":"Related"},"images":{"https://example.com/image.jpg":"Image"},"futureData":{"x":true}},"futureField":{"nested":[true,2]}}
        """;

    [Fact]
    public async Task Search_UsesLatestUserTextAndPassesUnknownNestedOptions()
    {
        var calls = new List<JsonElement>();
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            Assert.Equal("https://api.websearchapi.ai/ai-search", http.RequestUri!.AbsoluteUri);
            calls.Add(await ReadPayload(http, ct));
            return Json(SearchJson);
        });
        Assert.IsAssignableFrom<IUnifiedModelProvider>(provider);
        var response = await provider.ExecuteUnifiedAsync(Request("WebSearch", [Text("latest query")],
            new { includeAnswer = false, query = "ignored", arbitrary = new { values = new object[] { true, 3, "value" } } }));
        var payload = Assert.Single(calls);
        Assert.Equal("latest query", payload.GetProperty("query").GetString());
        Assert.False(payload.GetProperty("includeAnswer").GetBoolean());
        Assert.True(payload.GetProperty("includeContent").GetBoolean());
        Assert.Equal("markdown", payload.GetProperty("contentFormat").GetString());
        Assert.Equal(3, payload.GetProperty("arbitrary").GetProperty("values")[1].GetInt32());
        Assert.Null(response.Usage);
        var text = Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>());
        Assert.Contains("Grounded answer", text.Text);
        Assert.Contains("# Extracted content", text.Text);
        Assert.Contains("[Page](<https://example.com/final>)", text.Text);
        Assert.Single(response.Output.Items, item => item.Type == "source-url");
        var raw = JsonSerializer.SerializeToElement(response.Metadata!["websearchapi"]);
        Assert.True(raw.GetProperty("futureField").GetProperty("nested")[0].GetBoolean());
    }

    [Fact]
    public async Task Scrape_MakesOneRequestPerLatestHttpsFileUrlAndPreservesMetadata()
    {
        var calls = new List<JsonElement>();
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/scrape", http.RequestUri!.AbsolutePath);
            calls.Add(await ReadPayload(http, ct));
            var response = Json(ScrapeJson);
            response.Headers.Add("X-Credits-Consumed", "2");
            response.Headers.Add("X-Credits-Remaining", "98");
            return response;
        });
        var response = await provider.ExecuteUnifiedAsync(Request("WebScraper",
            [File("https://example.com/a"), File(new Uri("https://example.com/b")),
             File(JsonSerializer.SerializeToElement("https://example.com/c")), File("http://example.com/ignored"), Text("https://example.com/not-a-file")],
            new { url = "https://example.com/metadata-ignored", engine = "browser", viewport = new { width = 1000, height = 800 } }));
        Assert.Equal(new[] { "https://example.com/a", "https://example.com/b", "https://example.com/c" },
            calls.Select(p => p.GetProperty("url").GetString()));
        Assert.All(calls, p => Assert.Equal("markdown", p.GetProperty("returnFormat").GetString()));
        Assert.All(calls, p => Assert.Equal(1000, p.GetProperty("viewport").GetProperty("width").GetInt32()));
        Assert.Equal(3, response.Output!.Items![0].Content!.OfType<AITextContentPart>().Count());
        Assert.All(response.Output.Items[0].Content!.OfType<AITextContentPart>(), part =>
        {
            Assert.Contains("# Extracted content", part.Text);
            Assert.Contains("[Related](<https://example.com/link>)", part.Text);
            Assert.Contains("[Image](<https://example.com/image.jpg>)", part.Text);
            var metadata = JsonSerializer.SerializeToElement(part.Metadata!["websearchapi"]);
            Assert.Equal("2", metadata.GetProperty("X-Credits-Consumed").GetString());
            Assert.Equal("98", metadata.GetProperty("X-Credits-Remaining").GetString());
            Assert.True(metadata.GetProperty("data").GetProperty("futureData").GetProperty("x").GetBoolean());
            Assert.True(metadata.GetProperty("futureField").GetProperty("nested")[0].GetBoolean());
        });
        Assert.Equal(3, response.Output.Items.Count(item => item.Type == "source-url"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://example.com")]
    [InlineData("data:text/plain;base64,aGVsbG8=")]
    [InlineData("not a URL")]
    public async Task Scrape_RequiresLatestUserHttpsAttachmentBeforeMakingRequests(string? url)
    {
        var provider = Provider((_, _) => throw new InvalidOperationException("Must not send HTTP"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(
            Request("WebScraper", url is null ? [Text("https://example.com")] : [File(url)], new { url = "https://example.com/metadata" })));
        Assert.Contains("latest user message", error.Message);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("markdown")]
    public async Task Scrape_FormatOverrideIsPassedThrough(string format)
    {
        var provider = Provider(async (http, ct) =>
        {
            Assert.Equal(format, (await ReadPayload(http, ct)).GetProperty("returnFormat").GetString());
            return Json(ScrapeJson);
        });
        var response = await provider.ExecuteUnifiedAsync(Request("WebScraper", [File("https://example.com")], new { returnFormat = format }));
        Assert.Contains("# Extracted content", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scrape_StopsBatchOnHttpOrProviderFailure(bool providerFailure)
    {
        var count = 0;
        var provider = Provider((_, _) =>
        {
            count++;
            return Task.FromResult(count == 1 ? Json(ScrapeJson) : providerFailure
                ? Json("""{"code":429,"error":{"message":"rate limited"}}""")
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") });
        });
        var request = Request("WebScraper", [File("https://example.com/a"), File("https://example.com/b"), File("https://example.com/c")]);
        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(provider.StreamUnifiedAsync(request)));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Scrape_SyntheticStreamHasDistinctTextPartsSourcesAndOneFinish()
    {
        var provider = Provider((_, _) => Task.FromResult(Json(ScrapeJson)));
        var events = await Collect(provider.StreamUnifiedAsync(Request("WebScraper", [File("https://example.com/a"), File("https://example.com/b")])));
        var starts = events.Where(e => e.Event.Type == "text-start").ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(2, starts.Select(e => e.Event.Id).Distinct().Count());
        foreach (var start in starts)
        {
            Assert.Single(events, e => e.Event.Id == start.Event.Id && e.Event.Type == "text-delta");
            Assert.Single(events, e => e.Event.Id == start.Event.Id && e.Event.Type == "text-end");
        }
        Assert.Equal(2, events.Count(e => e.Event.Type == "source-url"));
        var source = (AISourceUrlEventData)events.First(e => e.Event.Type == "source-url").Event.Data!;
        Assert.Equal("https://example.com/final", source.Url);
        Assert.True(source.ProviderMetadata!["websearchapi"].ContainsKey("futureField"));
        Assert.Single(events, e => e.Event.Type == "finish");
        Assert.Contains("websearchapi", JsonSerializer.Serialize(((AIFinishEventData)events.Last().Event.Data!).MessageMetadata));
    }

    [Theory]
    [InlineData("WebSearch")]
    [InlineData("WebScraper")]
    public async Task AllFourEndpoints_UseUnifiedExecutionAndProduceReadableStreams(string model)
    {
        var count = 0;
        var provider = Provider(async (http, ct) =>
        {
            count++;
            Assert.True((await ReadPayload(http, ct)).GetProperty("custom").GetBoolean());
            return Json(model == "WebSearch" ? SearchJson : ScrapeJson);
        });
        var attachments = model == "WebScraper" ? ",{\"type\":\"file\",\"file\":{\"file_data\":\"https://example.com/a\"}},{\"type\":\"file\",\"file\":{\"file_data\":\"https://example.com/b\"}}" : "";
        var chat = JsonSerializer.Deserialize<ChatCompletionOptions>($$$$"""
            {"model":"websearchapi/{{{{model}}}}","messages":[{"role":"user","content":[{"type":"text","text":"query"}{{{{attachments}}}}]}],"metadata":{"websearchapi":{"custom":true}}}
            """, JsonSerializerOptions.Web)!;
        var responseAttachments = model == "WebScraper" ? ",{\"type\":\"input_file\",\"file_url\":\"https://example.com/a\"},{\"type\":\"input_file\",\"file_url\":\"https://example.com/b\"}" : "";
        var responses = JsonSerializer.Deserialize<ResponseRequest>($$$$"""
            {"model":"websearchapi/{{{{model}}}}","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"query"}{{{{responseAttachments}}}}]}],"metadata":{"websearchapi":{"custom":true}}}
            """, JsonSerializerOptions.Web)!;
        var messageAttachments = model == "WebScraper" ? ",{\"type\":\"document\",\"source\":{\"type\":\"url\",\"url\":\"https://example.com/a\"}},{\"type\":\"document\",\"source\":{\"type\":\"url\",\"url\":\"https://example.com/b\"}}" : "";
        var messages = JsonSerializer.Deserialize<MessagesRequest>($$$$"""
            {"model":"websearchapi/{{{{model}}}}","max_tokens":1000,"messages":[{"role":"user","content":[{"type":"text","text":"query"}{{{{messageAttachments}}}}]}],"metadata":{"websearchapi":{"custom":true}}}
            """, JsonSerializerOptions.Web)!;
        var ui = new ChatRequest
        {
            Model = $"websearchapi/{model}", ProviderMetadata = new() { ["websearchapi"] = JsonSerializer.SerializeToElement(new { custom = true }) },
            Messages = [new UIMessage { Id = "user", Role = Role.user, Parts = model == "WebScraper"
                ? [new TextUIPart { Text = "query" }, new FileUIPart { Url = "https://example.com/a", MediaType = "text/html" }, new FileUIPart { Url = "https://example.com/b", MediaType = "text/html" }]
                : [new TextUIPart { Text = "query" }] }]
        };
        var chatResult = await provider.CompleteChatAsync(chat);
        var responseResult = await provider.ResponsesAsync(responses);
        var messageResult = await provider.MessagesAsync(messages, []);
        var output = JsonSerializer.SerializeToElement(responseResult.Output);
        Assert.Equal("message", Assert.Single(output.EnumerateArray()).GetProperty("type").GetString());
        Assert.Equal(model == "WebScraper" ? 2 : 1, output[0].GetProperty("content").GetArrayLength());
        Assert.Contains("# Extracted content", JsonSerializer.Serialize(chatResult));
        Assert.Contains("# Extracted content", JsonSerializer.Serialize(responseResult));
        Assert.Contains("# Extracted content", JsonSerializer.Serialize(messageResult));
        Assert.Contains("futureField", JsonSerializer.Serialize(chatResult));
        Assert.Contains("futureField", JsonSerializer.Serialize(responseResult));
        Assert.Contains("futureField", JsonSerializer.Serialize(messageResult));
        var chatStream = await Collect(provider.CompleteChatStreamingAsync(chat));
        var responseStream = await Collect(provider.ResponsesStreamingAsync(responses));
        var messageStream = await Collect(provider.MessagesStreamingAsync(messages, []));
        var uiStream = await Collect(provider.StreamAsync(ui));
        Assert.Contains("# Extracted content", JsonSerializer.Serialize(chatStream));
        Assert.Contains("# Extracted content", JsonSerializer.Serialize(responseStream));
        Assert.Contains("# Extracted content", JsonSerializer.Serialize(messageStream));
        Assert.Contains(uiStream, p => p is SourceUIPart);
        Assert.Equal(model == "WebScraper" ? 2 : 1, uiStream.OfType<TextDeltaUIMessageStreamPart>().Count());
        Assert.Contains("futureField", JsonSerializer.Serialize(uiStream));
        Assert.Equal(model == "WebScraper" ? 14 : 7, count);
    }

    [Fact]
    public async Task CancellationAndUnknownModelsDoNotSendHttp()
    {
        var provider = Provider((_, _) => throw new InvalidOperationException("Must not send HTTP"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request("WebSearch", [Text("query")]), cts.Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(Request("unknown", [Text("query")])));
    }

    [Fact]
    public async Task ScreenshotBinaryResponseIsReturnedAsImageNotJsonOrMarkdown()
    {
        var provider = Provider((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("image/png");
            return Task.FromResult(response);
        });
        var result = await provider.ExecuteUnifiedAsync(Request("WebScraper", [File("https://example.com")], new { returnFormat = "screenshot" }));
        var file = Assert.Single(result.Output!.Items![0].Content!.OfType<AIFileContentPart>());
        Assert.Equal("data:image/png;base64,AQID", file.Data);
        Assert.Equal("image/png", file.MediaType);
    }

    private static AIRequest Request(string model, List<AIContentPart> parts, object? options = null) => new()
    {
        ProviderId = "websearchapi", Model = $"websearchapi/{model}",
        Input = new AIInput { Items = [new AIInputItem { Role = "user", Content = [File("https://example.com/old"), Text("old query")] },
            new AIInputItem { Role = "user", Content = parts }, new AIInputItem { Role = "assistant", Content = [File("https://example.com/assistant")] }] },
        Metadata = options is null ? null : new() { ["websearchapi"] = options }
    };
    private static AITextContentPart Text(string text) => new() { Type = "text", Text = text };
    private static AIFileContentPart File(object data) => new() { Type = "file", Data = data };
    private static async Task<JsonElement> ReadPayload(HttpRequestMessage request, CancellationToken ct)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private static WebsearchAPIProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))));
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name = "") => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request, cancellationToken); }
}
