using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Infron;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Infron;

public sealed class InfronProviderMediaTests
{
    [Fact]
    public async Task VideoRequest_text_to_video_posts_to_generations_endpoint_and_downloads_video()
    {
        var requestedPath = string.Empty;
        var requestJson = string.Empty;
        var expectedBytes = Encoding.UTF8.GetBytes("video-bytes");

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                requestedPath = request.RequestUri?.PathAndQuery ?? string.Empty;
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {"created":1761380356,"data":[{"url":"https://resource.onerouter.pro/video/generated/test.mp4","video_id":"video_123"}]}
                    """, Encoding.UTF8, MediaTypeNames.Application.Json)
                };
            }

            if (string.Equals(request.RequestUri?.AbsoluteUri, "https://resource.onerouter.pro/video/generated/test.mp4", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(expectedBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected request")
            };
        });

        var result = await provider.VideoRequest(new VideoRequest
        {
            Model = "veo3-fast",
            Prompt = "A cute baby sea otter"
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("/v1/videos/generations", requestedPath);
        Assert.Equal("veo3-fast", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("A cute baby sea otter", payload.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("url", payload.RootElement.GetProperty("output_format").GetString());

        var video = Assert.Single(result.Videos ?? []);
        Assert.Equal(Convert.ToBase64String(expectedBytes), video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task VideoRequest_with_video_id_posts_to_edits_endpoint()
    {
        var requestedPath = string.Empty;
        var requestJson = string.Empty;

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                requestedPath = request.RequestUri?.PathAndQuery ?? string.Empty;
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {"created":1761380356,"data":[{"data":"dmlkZW8tYnl0ZXM=","mime_type":"video/mp4","video_id":"video_123"}]}
                    """, Encoding.UTF8, MediaTypeNames.Application.Json)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected request")
            };
        });

        var result = await provider.VideoRequest(new VideoRequest
        {
            Model = "sora-2-video-to-video",
            Prompt = "A cute baby sea otter",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["infron"] = JsonSerializer.SerializeToElement(new
                {
                    video_id = "video_123"
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("/v1/videos/edits", requestedPath);
        Assert.Equal("video_123", payload.RootElement.GetProperty("video_id").GetString());
        Assert.Equal("video/mp4", Assert.Single(result.Videos ?? []).MediaType);
    }

    [Fact]
    public async Task RerankingRequest_sorts_scores_descending_and_applies_top_n()
    {
        var requestedPath = string.Empty;
        var requestJson = string.Empty;

        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery ?? string.Empty;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "results": [
                    {"index": 0, "relevance_score": 0.10},
                    {"index": 2, "relevance_score": 0.95},
                    {"index": 1, "relevance_score": 0.35}
                  ],
                  "usage": {"total_tokens": 32}
                }
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            };
        });

        var result = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "qwen/qwen3-reranker-0.6b",
            Query = "What is the capital of the United States?",
            TopN = 2,
            Documents = new RerankingDocument
            {
                Type = "json",
                Values = JsonSerializer.SerializeToElement(new[]
                {
                    "Carson City is the capital of Nevada.",
                    "Washington, D.C. is the capital of the United States.",
                    "Saipan is the capital of the Northern Mariana Islands."
                }, JsonSerializerOptions.Web)
            },
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["infron"] = JsonSerializer.SerializeToElement(new
                {
                    return_documents = true
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("/v1/rerank", requestedPath);
        Assert.Equal(2, payload.RootElement.GetProperty("top_n").GetInt32());
        Assert.True(payload.RootElement.GetProperty("return_documents").GetBoolean());

        var ranking = result.Ranking.ToArray();
        Assert.Equal([2, 1], ranking.Select(r => r.Index).ToArray());
        Assert.Equal("qwen/qwen3-reranker-0.6b", result.Response.ModelId);
        Assert.Contains(result.Warnings, warning => warning.ToString()?.Contains("documents.type", StringComparison.Ordinal) == true);
    }

    private static InfronProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new InfronProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
