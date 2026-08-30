using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Runware;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Runware;

public sealed class RunwareProviderVideoTests
{
    [Fact]
    public async Task StartVideoOperation_forces_async_and_returns_model_aware_token()
    {
        JsonElement sent = default;
        AuthenticationHeaderValue? authorization = null;
        var provider = CreateProvider(request =>
        {
            authorization = request.Headers.Authorization;
            sent = ReadJson(request)[0].Clone();
            return JsonResponse(new { data = new[] { new { taskType = "videoInference", taskUUID = "task-1", status = "processing" } } });
        });
        var options = JsonDocument.Parse("""{"deliveryMethod":"sync","outputFormat":"WEBM"}""").RootElement.Clone();

        var result = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "klingai:kling-video@3-4k",
            Prompt = "Ocean waves",
            ProviderOptions = new() { ["runware"] = options }
        });

        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("test-api-key", authorization?.Parameter);
        Assert.Equal("videoInference", sent.GetProperty("taskType").GetString());
        Assert.Equal("async", sent.GetProperty("deliveryMethod").GetString());
        Assert.Equal("WEBM", sent.GetProperty("outputFormat").GetString());
        Assert.StartsWith("rwav1_", result.Operation, StringComparison.Ordinal);
        Assert.Equal("runware/klingai:kling-video@3-4k", result.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_polls_task_uuid_and_preserves_exact_model_for_pending()
    {
        var requests = 0;
        JsonElement poll = default;
        var provider = CreateProvider(request =>
        {
            requests++;
            if (requests == 1)
                return JsonResponse(new { data = new[] { new { taskUUID = "task-model", status = "processing" } } });

            poll = ReadJson(request)[0].Clone();
            return JsonResponse(new { data = new[] { new { taskType = "videoInference", taskUUID = "task-model", status = "processing", progress = 47 } } });
        });
        var started = await provider.StartVideoOperation(new VideoRequest { Model = "private/model@42", Prompt = "Clouds" });

        var pending = Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("getResponse", poll.GetProperty("taskType").GetString());
        Assert.Equal("task-model", poll.GetProperty("taskUUID").GetString());
        Assert.Equal("runware/private/model@42", pending.Response.ModelId);
        Assert.Equal(47, Assert.Contains("runware", pending.ProviderMetadata!).GetProperty("data")[0].GetProperty("progress").GetInt32());
    }

    [Fact]
    public async Task GetVideoOperationStatus_maps_api_error_with_original_model()
    {
        var requests = 0;
        var provider = CreateProvider(_ => ++requests == 1
            ? JsonResponse(new { data = new[] { new { taskUUID = "task-error", status = "processing" } } })
            : JsonResponse(new { data = Array.Empty<object>(), errors = new[] { new { code = "timeoutProvider", status = "error", message = "Provider timed out", taskUUID = "task-error" } } }));
        var started = await provider.StartVideoOperation(new VideoRequest { Model = "xai:grok-imagine@video-1.5", Prompt = "Storm" });

        var error = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("Provider timed out", error.Error);
        Assert.Equal("runware/xai:grok-imagine@video-1.5", error.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_downloads_completed_video_and_preserves_model()
    {
        const string videoUrl = "https://media.runware.test/result.webm";
        var requests = 0;
        var provider = CreateProvider(request =>
        {
            requests++;
            if (requests == 1)
                return JsonResponse(new { data = new[] { new { taskUUID = "task-complete", status = "processing" } } });
            if (request.RequestUri?.Host == "media.runware.test")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            return JsonResponse(new { data = new[] { new { taskType = "videoInference", taskUUID = "task-complete", status = "success", videoURL = videoUrl } } });
        });
        var started = await provider.StartVideoOperation(new VideoRequest { Model = "alibaba:wan@3.0", Prompt = "Forest" });

        var completed = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("runware/alibaba:wan@3.0", completed.Response.ModelId);
        var video = Assert.Single(completed.Videos);
        Assert.Equal("video/webm", video.MediaType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), video.Data);
    }

    [Fact]
    public async Task GetVideoOperationStatus_accepts_data_uri_output()
    {
        var requests = 0;
        var provider = CreateProvider(_ => ++requests == 1
            ? JsonResponse(new { data = new[] { new { taskUUID = "task-data", status = "processing" } } })
            : JsonResponse(new { data = new[] { new { taskUUID = "task-data", status = "success", videoDataURI = "data:video/quicktime;base64,AQID" } } }));
        var started = await provider.StartVideoOperation(new VideoRequest { Model = "model-data", Prompt = "Test" });

        var completed = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus(started.Operation));
        var video = Assert.Single(completed.Videos);
        Assert.Equal("video/quicktime", video.MediaType);
        Assert.Equal("AQID", video.Data);
    }

    [Fact]
    public async Task GetVideoOperationStatus_rejects_legacy_and_malformed_tokens()
    {
        var provider = CreateProvider(_ => throw new Xunit.Sdk.XunitException("Backend must not be called."));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("legacy-task-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("rwav1_not-base64!"));
    }

    private static RunwareProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new SingleHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static JsonElement ReadJson(HttpRequestMessage request)
        => JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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
