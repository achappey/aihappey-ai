using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.RewindAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.RewindAI;

public sealed class RewindAIProviderVideoTests
{
    [Fact]
    public async Task StartVideoOperation_flattens_provider_metadata_standard_fields_win_and_preserves_model()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/videos/generate-async", request.RequestUri?.AbsolutePath);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.Accepted, """{"id":"job-1","status":"queued"}""");
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "bytedance/seedance-1-5-pro",
            Prompt = "A cat running",
            Duration = 5,
            AspectRatio = "16:9",
            ProviderOptions = new()
            {
                ["rewindai"] = JsonSerializer.SerializeToElement(new
                {
                    model = "wrong-model",
                    prompt = "wrong prompt",
                    duration = "99s",
                    custom_field = "passthrough",
                    nested = new { strength = 0.7 }
                })
            }
        });

        using var document = JsonDocument.Parse(Assert.IsType<string>(body));
        var root = document.RootElement;
        Assert.Equal("bytedance/seedance-1-5-pro", root.GetProperty("model").GetString());
        Assert.Equal("A cat running", root.GetProperty("prompt").GetString());
        Assert.Equal("5s", root.GetProperty("duration").GetString());
        Assert.Equal("16:9", root.GetProperty("aspectRatio").GetString());
        Assert.Equal("passthrough", root.GetProperty("custom_field").GetString());
        Assert.Equal(0.7, root.GetProperty("nested").GetProperty("strength").GetDouble());
        Assert.StartsWith("rwv1_", started.Operation);
        Assert.Equal("rewindai/bytedance/seedance-1-5-pro", started.Response.ModelId);
        Assert.Equal("job-1", started.ProviderMetadata!["rewindai"].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetVideoOperationStatus_uses_job_id_and_original_model_for_pending_status()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            if (calls == 1)
                return JsonResponse(HttpStatusCode.Accepted, """{"job_id":"job/pending"}""");

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/jobs/job%2Fpending", request.RequestUri?.AbsolutePath);
            return JsonResponse(HttpStatusCode.OK, """{"id":"job/pending","status":"processing"}""");
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "vendor/original-model",
            Prompt = "Clouds"
        });
        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("rewindai/vendor/original-model", pending.Response.ModelId);
        Assert.Equal("processing", pending.ProviderMetadata!["rewindai"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetVideoOperationStatus_maps_failure_with_original_model()
    {
        var calls = 0;
        var provider = CreateProvider(_ => ++calls == 1
            ? JsonResponse(HttpStatusCode.Accepted, """{"id":"job-failed"}""")
            : JsonResponse(HttpStatusCode.OK, """{"id":"job-failed","status":"failed","error":{"message":"capacity unavailable"}}"""));

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "failure-model",
            Prompt = "Ocean"
        });
        var failed = Assert.IsType<VideoOperationErrorResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("capacity unavailable", failed.Error);
        Assert.Equal("rewindai/failure-model", failed.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_downloads_completed_video_and_preserves_model()
    {
        var calls = 0;
        var video = new byte[] { 1, 2, 3, 4 };
        var provider = CreateProvider(request => ++calls switch
        {
            1 => JsonResponse(HttpStatusCode.Accepted, """{"job":{"id":"job-complete"}}"""),
            2 => JsonResponse(HttpStatusCode.OK, """{"id":"job-complete","status":"completed","output":{"video_url":"https://cdn.example/video.mp4"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(video)
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
        var output = Assert.Single(completed.Videos);

        Assert.Equal("base64", output.Type);
        Assert.Equal("video/mp4", output.MediaType);
        Assert.Equal(Convert.ToBase64String(video), output.Data);
        Assert.Equal("rewindai/completed-model", completed.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_rejects_non_model_aware_or_malformed_tokens()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No request expected."));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("legacy-job-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("rwv1_not-base64!"));
    }

    private static RewindAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
