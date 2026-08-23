using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Routmy;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Routmy;

public sealed class RoutmyProviderVideoTests
{
    [Fact]
    public async Task StartVideoOperation_flattens_provider_options_and_preserves_standard_fields()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/video/generations", request.RequestUri?.AbsolutePath);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.Accepted, """{"task_id":"task-1","status":"queued","custom_response":true}""");
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "bytedance/seedance-2.5",
            Prompt = "A glass cube",
            Duration = 5,
            AspectRatio = "16:9",
            GenerateAudio = true,
            ProviderOptions = new()
            {
                ["routmy"] = JsonSerializer.SerializeToElement(new
                {
                    model = "wrong-model",
                    prompt = "wrong prompt",
                    negative_prompt = "blur",
                    service_tier = "flex",
                    nested = new { strength = 0.7 }
                })
            }
        });

        using var document = JsonDocument.Parse(Assert.IsType<string>(body));
        var root = document.RootElement;
        Assert.Equal("bytedance/seedance-2.5", root.GetProperty("model").GetString());
        Assert.Equal("A glass cube", root.GetProperty("prompt").GetString());
        Assert.Equal(5, root.GetProperty("duration").GetInt32());
        Assert.Equal("16:9", root.GetProperty("aspect_ratio").GetString());
        Assert.True(root.GetProperty("audio").GetBoolean());
        Assert.Equal("blur", root.GetProperty("negative_prompt").GetString());
        Assert.Equal("flex", root.GetProperty("service_tier").GetString());
        Assert.Equal(0.7, root.GetProperty("nested").GetProperty("strength").GetDouble());
        Assert.StartsWith("rmv1_", started.Operation);
        Assert.Equal("routmy/bytedance/seedance-2.5", started.Response.ModelId);
        Assert.True(started.ProviderMetadata!["routmy"].GetProperty("custom_response").GetBoolean());
    }

    [Fact]
    public async Task GetVideoOperationStatus_uses_opaque_task_and_exact_original_model_for_pending_status()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            if (++calls == 1)
                return JsonResponse(HttpStatusCode.Accepted, """{"data":{"id":"task/pending"},"status":"queued"}""");

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/video/generations/task%2Fpending", request.RequestUri?.AbsolutePath);
            return JsonResponse(HttpStatusCode.OK, """{"data":{"status":"processing","progress":42}}""");
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "qwen/wan-v2.7",
            Prompt = "Rainy city"
        });
        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("routmy/qwen/wan-v2.7", pending.Response.ModelId);
        Assert.Equal(42, pending.ProviderMetadata!["routmy"].GetProperty("data").GetProperty("progress").GetInt32());
    }

    [Fact]
    public async Task GetVideoOperationStatus_maps_nested_failure_and_preserves_model()
    {
        var calls = 0;
        var provider = CreateProvider(_ => ++calls == 1
            ? JsonResponse(HttpStatusCode.Accepted, """{"request_id":"failed-task"}""")
            : JsonResponse(HttpStatusCode.OK, """{"status":"failed","error":{"message":"capacity unavailable"}}"""));

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "failure-model",
            Prompt = "Ocean"
        });
        var failed = Assert.IsType<VideoOperationErrorResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("capacity unavailable", failed.Error);
        Assert.Equal("routmy/failure-model", failed.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_downloads_completed_video_and_preserves_model()
    {
        var calls = 0;
        var bytes = new byte[] { 1, 2, 3, 4 };
        var provider = CreateProvider(request => ++calls switch
        {
            1 => JsonResponse(HttpStatusCode.Accepted, """{"taskId":"complete-task"}"""),
            2 => JsonResponse(HttpStatusCode.OK, """{"status":"completed","videos":[{"url":"https://cdn.example/video.mp4","mime_type":"video/mp4"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
                {
                    Headers = { ContentType = new("video/mp4") }
                }
            }
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "completed-model",
            Prompt = "Sunset"
        });
        var completed = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(started.Operation));
        var video = Assert.Single(completed.Videos);

        Assert.Equal("base64", video.Type);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(Convert.ToBase64String(bytes), video.Data);
        Assert.Equal("routmy/completed-model", completed.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_rejects_legacy_and_malformed_tokens()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No request expected."));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("legacy-task-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("rmv1_not-base64!"));
    }

    private static RoutmyProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
        => new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
