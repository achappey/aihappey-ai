using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.NinjaChat;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.NinjaChat;

public class NinjaChatProviderUnifiedTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_SearchModel_ReturnsAssistantMessageAndSourceItems()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider(request =>
        {
            requests.Add(request);
            return JsonResponse(CreateSearchResponseJson());
        });

        var response = await provider.ExecuteUnifiedAsync(CreateSearchRequest());

        Assert.Single(requests, request => request.RequestUri?.AbsolutePath == "/api/v1/search");
        Assert.Equal("completed", response.Status);
        Assert.Equal("search", response.Model);

        Assert.NotNull(response.Output);
        var outputItems = Assert.IsAssignableFrom<List<AIOutputItem>>(response.Output!.Items);
        var messageItem = Assert.Single(outputItems, item => item.Type == "message");
        var textPart = Assert.Single(messageItem.Content!.OfType<AITextContentPart>());
        Assert.Contains("Recent developments in AI safety include", textPart.Text);
        Assert.Contains("Sources:", textPart.Text);

        var filePart = Assert.Single(messageItem.Content!.OfType<AIFileContentPart>());
        Assert.Equal("image/png", filePart.MediaType);
        Assert.Equal("Safety-diagram.png", filePart.Filename);
        Assert.StartsWith("data:image/png;base64,", Assert.IsType<string>(filePart.Data));
        Assert.Equal("https://example.com/safety-diagram.png", filePart.Metadata!["ninjachat.image.origin_url"]?.ToString());

        var sourceItems = outputItems.Where(item => item.Type == "source-url").ToList();
        Assert.Equal(2, sourceItems.Count);
        Assert.Equal("https://example.com/article", sourceItems[0].Metadata!["chatcompletions.source.url"]?.ToString());
        Assert.Equal("AI Safety Progress in 2026", sourceItems[0].Metadata!["chatcompletions.source.title"]?.ToString());

        Assert.Equal("latest developments in AI safety", response.Metadata!["ninjachat.query"]?.ToString());
        Assert.Contains("ninjachat.downloaded_images", response.Metadata.Keys);
    }

    [Fact]
    public async Task StreamUnifiedAsync_SearchModel_EmitsDownloadedImageFileEvent()
    {
        var requests = new List<HttpRequestMessage>();
        var factory = new StaticHttpClientFactory(() => new HttpClient(new StaticResponseHttpMessageHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri?.AbsoluteUri == "https://example.com/safety-diagram.png"
                ? BinaryResponse([1, 2, 3], "image/png")
                : JsonResponse(CreateSearchResponseJson());
        })));

        var provider = CreateProvider(factory);

        var events = await CollectAsync(provider.StreamUnifiedAsync(CreateSearchRequest()));

        Assert.True(factory.CreateCount >= 2);
        Assert.Contains(requests, request => request.RequestUri?.AbsolutePath == "/api/v1/search");
        Assert.Contains(requests, request => request.RequestUri?.AbsoluteUri == "https://example.com/safety-diagram.png");

        var fileEvent = Assert.Single(events, e => e.Event.Type == "file");
        var fileData = Assert.IsType<AIFileEventData>(fileEvent.Event.Data);
        Assert.Equal("image/png", fileData.MediaType);
        Assert.Equal("Safety-diagram.png", fileData.Filename);
        Assert.StartsWith("data:image/png;base64,", fileData.Url);
        Assert.Equal("https://example.com/safety-diagram.png", fileData.ProviderMetadata!["ninjachat"]["origin_url"]?.ToString());
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_SearchModel_EmitsSourceDeltasAndFinish()
    {
        var provider = CreateProvider(_ => JsonResponse(CreateSearchResponseJson()));

        var updates = await CollectAsync(provider.CompleteChatStreamingAsync(
            CreateSearchRequest().ToChatCompletionOptions("ninjachat")));

        Assert.Contains(updates, update => HasRoleDelta(update, "assistant"));
        Assert.Contains(updates, update => HasSourceDelta(update, "https://example.com/article"));
        Assert.Contains(updates, update => HasSourceDelta(update, "https://example.com/rlhf"));
        Assert.Contains(updates, update => HasContentDelta(update, "Recent developments in AI safety include"));
        Assert.Contains(updates, update => HasFinishReason(update, "stop"));
    }

    [Fact]
    public async Task MessagesAsync_SearchModel_PreservesCitationsThroughUnifiedAdapter()
    {
        var provider = CreateProvider(_ => JsonResponse(CreateSearchResponseJson()));

        var response = await provider.MessagesAsync(
            CreateSearchRequest().ToMessagesRequest("ninjachat"),
            new Dictionary<string, string>());

        var block = Assert.Single(response.Content);
        Assert.Equal("text", block.Type);
        Assert.NotNull(block.Citations);
        Assert.Equal(2, block.Citations!.Count);
        Assert.Equal("https://example.com/article", block.Citations[0].Url);
        Assert.Equal("web_search_result_location", block.Citations[0].Type);
    }

    [Fact]
    public async Task ResponsesStreamingAsync_SearchModel_EmitsUrlAnnotationsFromUnifiedStream()
    {
        var provider = CreateProvider(_ => JsonResponse(CreateSearchResponseJson()));

        var parts = await CollectAsync(provider.ResponsesStreamingAsync(
            CreateSearchRequest().ToResponseRequest("ninjachat")));

        Assert.Contains(parts, part => part is ResponseOutputItemAdded);
        Assert.Equal(2, parts.OfType<ResponseOutputTextAnnotationAdded>().Count());
        Assert.Contains(parts.OfType<ResponseOutputTextAnnotationAdded>(), part =>
            part.Annotation.AdditionalProperties?["url"].GetString() == "https://example.com/article");
        Assert.Contains(parts, part => part is ResponseOutputTextDelta delta
            && delta.Delta.Contains("Recent developments in AI safety include", StringComparison.Ordinal));
        Assert.Contains(parts, part => part is ResponseCompleted);
    }

    private static AIRequest CreateSearchRequest()
        => new()
        {
            ProviderId = "ninjachat",
            Model = "search",
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
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "latest developments in AI safety"
                            }
                        ]
                    }
                ]
            },
            Metadata = new Dictionary<string, object?>
            {
                ["ninjachat"] = new Dictionary<string, object?>
                {
                    ["group"] = "web",
                    ["include_answer"] = true,
                    ["include_images"] = true,
                    ["max_results"] = 2
                }
            }
        };

    private static NinjaChatProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(new StaticHttpClientFactory(() => new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static NinjaChatProvider CreateProvider(IHttpClientFactory httpClientFactory)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            httpClientFactory);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage BinaryResponse(byte[] bytes, string mediaType)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) }
            }
        };

    private static string CreateSearchResponseJson()
        => """
           {
             "query": "latest developments in AI safety",
             "answer": "Recent developments in AI safety include improved evaluations, stronger governance work, and better monitoring systems.",
             "sources": [
               {
                 "url": "https://example.com/article",
                 "title": "AI Safety Progress in 2026",
                 "content": "Summary of the article...",
                 "published_date": "2026-03-01"
               },
               {
                 "url": "https://example.com/rlhf",
                 "title": "RLHF and Safer Model Behavior",
                 "content": "RLHF improves alignment and behavior shaping.",
                 "published_date": "2026-02-10"
               }
             ],
             "follow_up_questions": [
               "What are the key AI safety organizations?",
               "How does RLHF improve AI safety?"
             ],
             "images": [
               {
                 "url": "https://example.com/safety-diagram.png",
                 "description": "Safety diagram"
               }
             ],
             "cost": { "this_request": "$0.05" },
             "metadata": {
               "group": "web",
               "search_depth": "basic",
               "results_count": 2,
               "latency_ms": 2341
             }
           }
           """;

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);

        return items;
    }

    private static bool HasRoleDelta(ChatCompletionUpdate update, string expectedRole)
    {
        var choice = GetFirstChoice(update);
        return choice.TryGetProperty("delta", out var delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("role", out var role)
            && string.Equals(role.GetString(), expectedRole, StringComparison.Ordinal);
    }

    private static bool HasContentDelta(ChatCompletionUpdate update, string expectedText)
    {
        var choice = GetFirstChoice(update);
        return choice.TryGetProperty("delta", out var delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("content", out var content)
            && content.GetString()?.Contains(expectedText, StringComparison.Ordinal) == true;
    }

    private static bool HasSourceDelta(ChatCompletionUpdate update, string expectedUrl)
    {
        var choice = GetFirstChoice(update);
        if (!choice.TryGetProperty("delta", out var delta)
            || delta.ValueKind != JsonValueKind.Object
            || !delta.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return sources.EnumerateArray().Any(source =>
            source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("url", out var url)
            && string.Equals(url.GetString(), expectedUrl, StringComparison.Ordinal));
    }

    private static bool HasFinishReason(ChatCompletionUpdate update, string expectedFinishReason)
    {
        var choice = GetFirstChoice(update);
        return choice.TryGetProperty("finish_reason", out var finishReason)
            && string.Equals(finishReason.GetString(), expectedFinishReason, StringComparison.Ordinal);
    }

    private static JsonElement GetFirstChoice(ChatCompletionUpdate update)
    {
        var root = JsonSerializer.SerializeToElement(update, JsonSerializerOptions.Web);
        var choices = root.GetProperty("choices");
        return choices.EnumerateArray().First();
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string Resolve(string apiProviderName) => "test-key";
    }

    private sealed class StaticHttpClientFactory(Func<HttpClient> createClient) : IHttpClientFactory
    {
        public int CreateCount { get; private set; }

        public HttpClient CreateClient(string name = "")
        {
            CreateCount++;
            return createClient();
        }
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
