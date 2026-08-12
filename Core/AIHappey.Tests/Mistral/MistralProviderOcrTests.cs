using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Mistral;

public sealed class MistralProviderOcrTests
{
    [Fact]
    public async Task ExecuteOcrProcessesAllLatestUserFilesAndMapsToolMarkdownAndImages()
    {
        var requests = new List<string>();
        var image = Convert.ToBase64String([1, 2, 3]);
        var responses = new Queue<string>(
        [
            JsonSerializer.Serialize(new
            {
                pages = new[] { new { index = 0, markdown = "# First", images = new[] { new { id = "figure.jpeg", image_base64 = image } } } },
                model = "mistral-ocr-latest",
                usage_info = new { pages_processed = 1 }
            }),
            """{"pages":[{"index":0,"markdown":"# Second","images":[]}],"model":"mistral-ocr-latest","usage_info":{"pages_processed":1}}"""
        ]);
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("https://api.mistral.ai/v1/ocr", request.RequestUri!.AbsoluteUri);
            requests.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        });

        var secret = Convert.ToBase64String([9, 8, 7]);
        var result = await provider.ExecuteUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", Filename = "ignored.pdf", MediaType = "application/pdf", Data = Convert.ToBase64String([0]) }]
            },
            new AIInputItem
            {
                Role = "user",
                Content =
                [
                    new AIFileContentPart { Type = "file", Filename = "first.pdf", MediaType = "application/pdf", Data = secret },
                    new AIFileContentPart { Type = "file", Filename = "second.png", MediaType = "image/png", Data = "data:image/png;base64," + Convert.ToBase64String([6]) }
                ]
            }));

        Assert.Equal(2, requests.Count);
        Assert.All(requests, body =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("mistral-ocr-latest", json.RootElement.GetProperty("model").GetString());
            Assert.True(json.RootElement.GetProperty("include_image_base64").GetBoolean());
        });
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));

        var items = result.Output!.Items!;
        Assert.Equal(4, items.Count);
        var tool = Assert.Single(items[0].Content!.OfType<AIToolCallContentPart>());
        Assert.True(tool.ProviderExecuted);
        Assert.Equal("mistral_ocr", tool.ToolName);
        var toolResult = Assert.IsType<CallToolResult>(tool.Output);
        Assert.Equal("mistral-ocr-latest", toolResult.StructuredContent!.Value.GetProperty("model").GetString());

        Assert.Equal("# First", Assert.Single(items[1].Content!.OfType<AITextContentPart>()).Text);
        var returnedImage = Assert.Single(items[1].Content!.OfType<AIFileContentPart>());
        Assert.Equal("image/jpeg", returnedImage.MediaType);
        Assert.Equal("figure.jpeg", returnedImage.Filename);
        Assert.Equal("# Second", Assert.Single(items[3].Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task ExecuteOcrRejectsRemoteUrlsBeforeSending()
    {
        var called = false;
        var provider = CreateProvider(_ =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", Filename = "remote.pdf", MediaType = "application/pdf", Data = "https://example.com/a.pdf" }]
            })));

        Assert.Contains("remote URL", exception.Message, StringComparison.Ordinal);
        Assert.False(called);
    }

    [Fact]
    public async Task StreamOcrEmitsToolTextImageAndSingleFinishInOrder()
    {
        var image = Convert.ToBase64String([4, 5]);
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    pages = new[] { new { index = 0, markdown = "text", images = new[] { new { id = "image.png", image_base64 = image } } } },
                    model = "mistral-ocr-latest"
                }),
                Encoding.UTF8,
                "application/json")
        }));

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", Filename = "one.pdf", MediaType = "application/pdf", Data = Convert.ToBase64String([1]) }]
            })))
            events.Add(item);

        Assert.Equal(
            ["tool-input-available", "tool-output-available", "text-start", "text-delta", "text-end", "file", "finish"],
            events.Select(item => item.Event.Type));
    }

    private static AIRequest CreateRequest(params AIInputItem[] items)
        => new()
        {
            ProviderId = "mistral",
            Model = "mistral/MISTRAL-OCR-LATEST",
            Input = new AIInput { Items = [.. items] }
        };

    private static MistralProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
