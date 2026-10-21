using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIProviderMediaDownloadTests
{
    [Fact]
    public async Task ResponsesAsync_preserves_web_search_image_urls_without_downloading_them()
    {
        var requests = new List<(HttpMethod Method, string Path)>();
        var provider = CreateProvider(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": "resp_123",
                      "object": "response",
                      "created_at": 1,
                      "status": "completed",
                      "model": "gpt-test",
                      "output": [
                        {
                          "id": "web_1",
                          "type": "web_search_call",
                          "status": "completed",
                          "results": [
                            {
                              "type": "image_result",
                              "image_url": "https://media.example/image.png",
                              "thumbnail_url": "https://media.example/thumbnail.png",
                              "source_website_url": "https://source.example/article",
                              "caption": "Search result"
                            }
                          ],
                          "action": { "type": "search", "query": "example" }
                        }
                      ],
                      "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var response = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "openai/gpt-test",
            Input = "Find an image."
        });

        Assert.Equal([(HttpMethod.Post, "/v1/responses")], requests);
        var output = JsonSerializer.Serialize(response.Output, JsonSerializerOptions.Web);
        Assert.Contains("https://media.example/image.png", output);
        Assert.Contains("https://media.example/thumbnail.png", output);
        Assert.Contains("https://source.example/article", output);
    }

    private static OpenAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new TestHttpClientFactory(new RecordingHandler(responder)),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticEndUserIdResolver());

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
