using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ScrapeLLM;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ScrapeLLM;

public class ScrapeLLMProviderUnifiedTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_UsesLatestUserMessageAndPrefersResultMarkdown()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = request;
            return JsonResponse(
                """
                {
                  "scraper":"chatgpt",
                  "status":"done",
                  "job_id":"job_1",
                  "result":"plain result",
                  "result_markdown":"**markdown result**",
                  "links":[{"text":"Docs","url":"https://example.com/docs"}],
                  "credits_used":3,
                  "elapsed_ms":1234.5,
                  "cached":false
                }
                """);
        });

        var request = new AIRequest
        {
            ProviderId = "scrapellm",
            Model = "scrapellm/chatgpt",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "old prompt" }
                        ]
                    },
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "assistant" }
                        ]
                    },
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "new prompt line 1" },
                            new AITextContentPart { Type = "text", Text = "new prompt line 2" }
                        ]
                    }
                ]
            }
        };

        var response = await provider.ExecuteUnifiedAsync(request);

        Assert.NotNull(captured);
        Assert.Equal("https://api.scrapellm.com/scrapers/chatgpt?prompt=new%20prompt%20line%201%0Anew%20prompt%20line%202&country=US&markdown_json=true", captured!.RequestUri!.AbsoluteUri);

        var message = Assert.Single(response.Output!.Items!, item => item.Type == "message");
        var text = Assert.Single(message.Content!.OfType<AITextContentPart>());
        Assert.Equal("**markdown result**", text.Text);

        var source = Assert.Single(response.Output.Items, item => item.Type == "source-url");
        Assert.Equal("https://example.com/docs", source.Metadata!["chatcompletions.source.url"]?.ToString());
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_RufusProducesJsonCodeBlockAndSourceUrls()
    {
        var provider = CreateProvider(_ =>
            JsonResponse(
                """
                {
                  "scraper":"amazon_rufus",
                  "status":"done",
                  "job_id":"job_rufus",
                  "products":[
                    {
                      "asin":"A1",
                      "title":"Headphones A",
                      "url":"https://amazon.example/a1",
                      "rating":"4.4",
                      "reviews":"100"
                    },
                    {
                      "asin":"A2",
                      "title":"Headphones B",
                      "url":"https://amazon.example/a2",
                      "rating":"4.5",
                      "reviews":"200"
                    }
                  ],
                  "related_questions":["Which has better ANC?"],
                  "credits_used":3
                }
                """));

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "scrapellm",
            Model = "scrapellm/amazon-rufus",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "best headphones under 100" }
                        ]
                    }
                ]
            }
        });

        var message = Assert.Single(response.Output!.Items!, item => item.Type == "message");
        var text = Assert.Single(message.Content!.OfType<AITextContentPart>()).Text;
        Assert.StartsWith("```json", text);
        Assert.Contains("\"products\"", text, StringComparison.Ordinal);
        Assert.Contains("\"related_questions\"", text, StringComparison.Ordinal);

        var urls = response.Output.Items
            .Where(item => item.Type == "source-url")
            .Select(item => item.Metadata!["chatcompletions.source.url"]?.ToString())
            .ToList();

        Assert.Contains("https://amazon.example/a1", urls);
        Assert.Contains("https://amazon.example/a2", urls);
    }

    [Fact]
    public async Task StreamUnifiedAsync_MimicsSyntheticStreamingContract()
    {
        var provider = CreateProvider(_ =>
            JsonResponse(
                """
                {
                  "scraper":"perplexity",
                  "status":"done",
                  "job_id":"job_stream",
                  "result_markdown":"stream body",
                  "sources":[{"title":"S1","url":"https://example.com/s1"}]
                }
                """));

        var events = await CollectAsync(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "scrapellm",
            Model = "scrapellm/perplexity",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "prompt" }
                        ]
                    }
                ]
            }
        }));

        Assert.True(events.Count >= 5);
        Assert.Equal("text-start", events[0].Event.Type);
        Assert.Equal("text-delta", events[1].Event.Type);
        Assert.Equal("text-end", events[2].Event.Type);
        Assert.Contains(events, e => e.Event.Type == "source-url");
        Assert.Equal("finish", events[^1].Event.Type);

        var delta = Assert.IsType<AITextDeltaEventData>(events[1].Event.Data);
        Assert.Equal("stream body", delta.Delta);
    }

    private static ScrapeLLMProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name = "") => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
