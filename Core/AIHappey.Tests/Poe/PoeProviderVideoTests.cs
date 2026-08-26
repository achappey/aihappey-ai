using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Poe;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Poe;

public sealed class PoeProviderVideoTests
{
    [Fact]
    public async Task StartVideoOperation_maps_request_and_returns_model_aware_token()
    {
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/videos", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {"id":"video_123","status":"queued","created_at":1704825600,"model":"Sora-2","progress":0}
                """);
        });

        var result = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "poe/Sora-2",
            Prompt = "A dog running through flowers",
            Duration = 8,
            Resolution = "1280x720",
            Image = new VideoFile
            {
                MediaType = "image/png",
                Data = "data:image/png;base64,AQID"
            },
            AspectRatio = "16:9",
            Seed = 42,
            Fps = 24,
            N = 2,
            InputReferences = [new VideoFile { MediaType = "image/png", Data = "AQID" }],
            FrameImages = [new VideoFrameImage { FrameType = "first_frame", Image = new VideoFile { MediaType = "image/png", Data = "AQID" } }],
            GenerateAudio = true
        });

        using var document = JsonDocument.Parse(requestJson!);
        var root = document.RootElement;
        Assert.Equal("Sora-2", root.GetProperty("model").GetString());
        Assert.Equal("A dog running through flowers", root.GetProperty("prompt").GetString());
        Assert.Equal(8, root.GetProperty("seconds").GetInt32());
        Assert.Equal("1280x720", root.GetProperty("size").GetString());
        Assert.Equal("AQID", root.GetProperty("input_image").GetString());
        Assert.StartsWith("poev1_", result.Operation);
        Assert.Equal("poe/Sora-2", result.Response.ModelId);
        Assert.Equal(7, result.Warnings.Count());
    }

    [Fact]
    public async Task GetVideoOperationStatus_preserves_model_and_downloads_completed_video_as_base64()
    {
        var requests = new List<string>();
        var provider = CreateProvider(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.PathAndQuery switch
            {
                "/v1/videos" => JsonResponse("""
                    {"id":"video_abc123","status":"queued","created_at":1704825600,"model":"Veo-3.1-Fast"}
                    """),
                "/v1/videos/video_abc123" => JsonResponse("""
                    {"id":"video_abc123","status":"completed","created_at":1704825600,"completed_at":1704825900,"model":"unexpected-model","progress":100}
                    """),
                "/v1/videos/video_abc123/content" => VideoResponse([1, 2, 3, 4]),
                _ => Unexpected(request)
            };
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "Veo-3.1-Fast",
            Prompt = "A cinematic landscape"
        });
        var result = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("poe/Veo-3.1-Fast", result.Response.ModelId);
        var video = Assert.Single(result.Videos);
        Assert.Equal("base64", video.Type);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), video.Data);
        Assert.Equal(
            ["/v1/videos", "/v1/videos/video_abc123", "/v1/videos/video_abc123/content"],
            requests);
    }

    [Fact]
    public async Task GetVideoOperationStatus_rejects_non_opaque_operation_id()
    {
        var provider = CreateProvider(Unexpected);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetVideoOperationStatus("video_abc123"));

        Assert.Contains("model-aware Poe video operation token", exception.Message);
    }

    private static PoeProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var factory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new PoeProvider(new StaticApiKeyResolver(), cache, factory);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpResponseMessage VideoResponse(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("video/mp4");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Unexpected(HttpRequestMessage request)
        => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected request: {request.Method} {request.RequestUri}")
        };

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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
