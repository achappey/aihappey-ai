using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Qiniu;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Qiniu;

public sealed class QiniuProviderVideoTests
{
    [Fact]
    public async Task StartVideoOperation_passes_raw_options_through_and_standard_fields_win()
    {
        JsonElement? sent = null;
        AuthenticationHeaderValue? authorization = null;
        var api = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/videos", request.RequestUri?.AbsolutePath);
            authorization = request.Headers.Authorization;
            sent = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse(new { id = "task-1", status = "queued", created_at = 1_766_391_125L });
        }));
        var provider = CreateProvider(api, new HttpClient());
        var raw = JsonDocument.Parse("""{"model":"wrong","prompt":"wrong","seconds":"10","size":"720x1280","mode":"pro","video_list":[{"video_url":"https://example.test/input.mp4"}]}""").RootElement.Clone();

        var result = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "kling-video-o1",
            Prompt = "correct prompt",
            Duration = 5,
            Resolution = "1280x720",
            ProviderOptions = new() { ["qiniu"] = raw },
            FrameImages = [new() { FrameType = "last_frame", Image = new() { MediaType = "image/png", Data = "data:image/png;base64,dGFpbA==" } }]
        });

        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("test-api-key", authorization?.Parameter);
        Assert.StartsWith("qnv1_", result.Operation);
        Assert.Equal("kling-video-o1", sent!.Value.GetProperty("model").GetString());
        Assert.Equal("correct prompt", sent.Value.GetProperty("prompt").GetString());
        Assert.Equal("5", sent.Value.GetProperty("seconds").GetString());
        Assert.Equal("1280x720", sent.Value.GetProperty("size").GetString());
        Assert.Equal("pro", sent.Value.GetProperty("mode").GetString());
        Assert.Equal("https://example.test/input.mp4", sent.Value.GetProperty("video_list")[0].GetProperty("video_url").GetString());
        Assert.Equal("dGFpbA==", sent.Value.GetProperty("image_list")[0].GetProperty("image").GetString());
        Assert.Equal("end_frame", sent.Value.GetProperty("image_list")[0].GetProperty("type").GetString());
        Assert.Equal("qiniu/kling-video-o1", result.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_uses_original_model_and_downloads_without_api_auth()
    {
        const string url = "https://result.qnaigc.test/video.mov?token=signed";
        AuthenticationHeaderValue? statusAuthorization = null;
        AuthenticationHeaderValue? downloadAuthorization = null;
        var api = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse(new { id = "task-model", status = "queued", created_at = 1_766_391_125L });
            Assert.Equal("/v1/videos/task-model", request.RequestUri?.AbsolutePath);
            statusAuthorization = request.Headers.Authorization;
            return JsonResponse(new
            {
                id = "task-model",
                model = "routed-model-that-must-not-win",
                status = "completed",
                completed_at = 1_766_391_255L,
                task_result = new { videos = new[] { new { url } } }
            });
        }));
        byte[] expected = [1, 2, 3, 4];
        var downloader = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            downloadAuthorization = request.Headers.Authorization;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("video/quicktime") }
                }
            };
        }));
        var provider = CreateProvider(api, downloader);
        var started = await provider.StartVideoOperation(new VideoRequest { Model = "kling-v2-5-turbo", Prompt = "test" });

        var result = await provider.GetVideoOperationStatus(started.Operation);

        Assert.Equal("Bearer", statusAuthorization?.Scheme);
        Assert.Null(downloadAuthorization);
        var completed = Assert.IsType<VideoOperationCompletedResult>(result);
        Assert.Equal("qiniu/kling-v2-5-turbo", completed.Response.ModelId);
        var video = Assert.Single(completed.Videos);
        Assert.Equal("video/quicktime", video.MediaType);
        Assert.Equal(Convert.ToBase64String(expected), video.Data);
    }

    [Fact]
    public async Task GetVideoOperationStatus_maps_pending_and_failure()
    {
        var responses = new Queue<HttpResponseMessage>([
            JsonResponse(new { id = "pending", status = "queued" }),
            JsonResponse(new { id = "pending", status = "uploading" }),
            JsonResponse(new { id = "failed", status = "queued" }),
            JsonResponse(new { id = "failed", status = "failed", error = new { message = "Kling failed" } })
        ]);
        var provider = CreateProvider(new HttpClient(new DelegateHttpMessageHandler(_ => responses.Dequeue())), new HttpClient());
        var pendingOperation = await provider.StartVideoOperation(new VideoRequest { Model = "kling-video-o1", Prompt = "pending" });
        Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus(pendingOperation.Operation));
        var failedOperation = await provider.StartVideoOperation(new VideoRequest { Model = "kling-v2-1", Prompt = "failed" });
        var failed = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus(failedOperation.Operation));
        Assert.Equal("Kling failed", failed.Error);
        Assert.Equal("qiniu/kling-v2-1", failed.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_rejects_raw_task_id_to_prevent_wrong_model_metadata()
    {
        var provider = CreateProvider(new HttpClient(), new HttpClient());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("qvideo-legacy-id"));
        Assert.Contains("model-aware token", error.Message);
    }

    private static QiniuProvider CreateProvider(HttpClient api, HttpClient downloader)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new SequencedHttpClientFactory(api, downloader));

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class SequencedHttpClientFactory(params HttpClient[] clients) : IHttpClientFactory
    {
        private int _index;
        public HttpClient CreateClient(string name) => clients[_index++];
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
