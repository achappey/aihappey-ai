using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.CloudFerro;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.CloudFerro;

public sealed class CloudFerroProviderOcrTests
{
    [Fact]
    public async Task ExecuteUnifiedOcrSendsLatestUserFilesAndReturnsSeparateOrderedMessages()
    {
        var requests = new List<CapturedRequest>();
        var responses = new Queue<string>(["# First", "# Second"]);
        var provider = CreateProvider(async request =>
        {
            requests.Add(await CapturedRequest.CreateAsync(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "text/markdown")
            };
        });

        var result = await provider.ExecuteUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", Filename = "ignored.png", MediaType = "image/png", Data = Convert.ToBase64String([9]) }]
            },
            new AIInputItem
            {
                Role = "assistant",
                Content = [new AITextContentPart { Text = "previous", Type = "text" }]
            },
            new AIInputItem
            {
                Role = "user",
                Content =
                [
                    new AIFileContentPart { Type = "file", Filename = "first.png", MediaType = "image/png", Data = Convert.ToBase64String([1, 2]) },
                    new AIFileContentPart { Type = "file", Filename = "second.pdf", MediaType = "application/pdf", Data = "data:application/pdf;base64," + Convert.ToBase64String([3, 4]) }
                ]
            }));

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api-sherlock.cloudferro.com/vision/v1/ocr/olmOCR-7B-0225-preview", request.Uri);
            Assert.Equal("Bearer", request.Authorization?.Scheme);
            Assert.Equal("test-key", request.Authorization?.Parameter);
            Assert.Contains("text/markdown", request.Accept);
        });
        Assert.Contains("name=file", requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("filename=first.png", requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("filename=second.pdf", requests[1].Body, StringComparison.Ordinal);

        var items = Assert.IsType<List<AIOutputItem>>(result.Output?.Items);
        Assert.Equal(2, items.Count);
        Assert.Equal("# First", Assert.Single(items[0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal("# Second", Assert.Single(items[1].Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task ExecuteUnifiedOcrRejectsRemoteUrlsBeforeSending()
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
                Content = [new AIFileContentPart { Type = "file", Filename = "remote.png", MediaType = "image/png", Data = "https://example.com/file.png" }]
            })));

        Assert.Contains("remote URL", exception.Message, StringComparison.Ordinal);
        Assert.False(called);
    }

    [Fact]
    public async Task StreamUnifiedOcrEmitsOneTextSequencePerFileAndOneFinish()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("markdown", Encoding.UTF8, "text/markdown")
        }));

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(CreateRequest(
            new AIInputItem
            {
                Role = "user",
                Content =
                [
                    new AIFileContentPart { Type = "file", Filename = "one.png", MediaType = "image/png", Data = Convert.ToBase64String([1]) },
                    new AIFileContentPart { Type = "file", Filename = "two.png", MediaType = "image/png", Data = Convert.ToBase64String([2]) }
                ]
            })))
            events.Add(item);

        Assert.Equal(
            ["text-start", "text-delta", "text-end", "text-start", "text-delta", "text-end", "finish"],
            events.Select(item => item.Event.Type));
    }

    private static AIRequest CreateRequest(params AIInputItem[] items)
        => new()
        {
            ProviderId = "cloudferro",
            Model = "cloudferro/olmOCR-7B-0225-preview",
            Input = new AIInput { Items = [.. items] }
        };

    private static CloudFerroProvider CreateProvider(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        AuthenticationHeaderValue? Authorization,
        string Accept,
        string Body)
    {
        public static async Task<CapturedRequest> CreateAsync(HttpRequestMessage request)
            => new(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization,
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync());
    }
}
