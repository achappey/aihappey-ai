using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.NetMind;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.NetMind;

public sealed class NetMindProviderVideoTests
{
    [Fact]
    public async Task Start_posts_async_generation_payload_and_preserves_model_in_operation()
    {
        const string generationId = "generation/123";
        const string model = "provider/video-model";
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var requestCount = 0;

        var provider = CreateProvider(request =>
        {
            requestCount++;
            capturedRequest = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { id = generationId, status = "pending", result = new { } });
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = model,
            Prompt = "A fox running through snow",
            Duration = 6,
            Resolution = "720p",
            AspectRatio = "16:9",
            Seed = 42
        });

        Assert.Equal(1, requestCount);
        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("/v1/generation", capturedRequest?.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.StartsWith("nmv1_", started.Operation, StringComparison.Ordinal);
        Assert.DoesNotContain('/', started.Operation);
        Assert.DoesNotContain('+', started.Operation);
        Assert.DoesNotContain('=', started.Operation);
        Assert.Equal($"netmind/{model}", started.Response.ModelId);

        using var payload = JsonDocument.Parse(capturedBody!);
        Assert.Equal(model, payload.RootElement.GetProperty("model").GetString());
        var config = payload.RootElement.GetProperty("config");
        Assert.Equal("A fox running through snow", config.GetProperty("prompt").GetString());
        Assert.Equal(6, config.GetProperty("duration").GetInt32());
        Assert.Equal("720p", config.GetProperty("resolution").GetString());
        Assert.Equal("16:9", config.GetProperty("aspect_ratio").GetString());
        Assert.Equal(42, config.GetProperty("seed").GetInt32());
    }

    [Fact]
    public async Task Status_uses_token_model_when_poll_response_has_no_model()
    {
        const string generationId = "generation-123";
        const string model = "creator/video-model";
        var requestCount = 0;
        var provider = CreateProvider(request =>
        {
            requestCount++;
            return requestCount == 1
                ? JsonResponse(new { id = generationId, status = "pending" })
                : JsonResponse(new { id = generationId, status = "processing", result = new { } });
        });

        var started = await provider.StartVideoOperation(new VideoRequest { Model = model, Prompt = "A cat" });
        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal($"netmind/{model}", pending.Response.ModelId);
    }

    [Fact]
    public async Task Status_prefers_model_returned_by_polling()
    {
        const string generationId = "generation-123";
        var requestCount = 0;
        var provider = CreateProvider(request =>
        {
            requestCount++;
            return requestCount == 1
                ? JsonResponse(new { id = generationId, status = "pending" })
                : JsonResponse(new { id = generationId, model = "actual/video-model", status = "running" });
        });

        var started = await provider.StartVideoOperation(new VideoRequest { Model = "requested/video-model", Prompt = "A cat" });
        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("netmind/actual/video-model", pending.Response.ModelId);
    }

    [Fact]
    public async Task Failed_status_maps_to_error_result()
    {
        var provider = CreateProvider(_ => JsonResponse(new
        {
            id = "failed-generation",
            model = "video-model",
            status = "failed",
            result = new { error = "Generation failed" }
        }));

        var error = Assert.IsType<VideoOperationErrorResult>(
            await provider.GetVideoOperationStatus("failed-generation"));

        Assert.Contains("failed", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("netmind/video-model", error.Response.ModelId);
    }

    [Fact]
    public async Task Completed_status_downloads_video_then_deletes_generation()
    {
        const string generationId = "completed-generation";
        const string downloadUrl = "https://files.netmind.test/output.mp4";
        var calls = new List<(HttpMethod Method, string Path)>();
        var videoBytes = new byte[] { 1, 2, 3, 4 };
        var provider = CreateProvider(request =>
        {
            calls.Add((request.Method, request.RequestUri!.AbsolutePath));
            if (request.Method == HttpMethod.Get && request.RequestUri.Host == "api.netmind.ai")
            {
                return JsonResponse(new
                {
                    id = generationId,
                    status = "completed",
                    result = new { data = new[] { new { url = downloadUrl, file_type = "video" } } }
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.Host == "files.netmind.test")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(videoBytes)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("video/webm") }
                    }
                };
            }

            Assert.Equal(HttpMethod.Delete, request.Method);
            return JsonResponse(new { message = "Generation deleted" });
        });

        var completed = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(generationId));

        var video = Assert.Single(completed.Videos);
        Assert.Equal("base64", video.Type);
        Assert.Equal("video/webm", video.MediaType);
        Assert.Equal(Convert.ToBase64String(videoBytes), video.Data);
        Assert.Equal(
            new[]
            {
                (HttpMethod.Get, $"/v1/generation/{generationId}"),
                (HttpMethod.Get, "/output.mp4"),
                (HttpMethod.Delete, $"/v1/generation/{generationId}")
            },
            calls);
    }

    [Fact]
    public async Task Legacy_raw_generation_id_remains_pollable()
    {
        const string generationId = "legacy generation/id";
        var provider = CreateProvider(request =>
        {
            Assert.Equal($"/v1/generation/{Uri.EscapeDataString(generationId)}", request.RequestUri?.AbsolutePath);
            return JsonResponse(new { id = generationId, status = "pending" });
        });

        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(Uri.EscapeDataString(generationId)));

        Assert.Equal("netmind", pending.Response.ModelId);
    }

    [Fact]
    public async Task Invalid_operation_envelope_is_rejected_before_polling()
    {
        var provider = CreateProvider(_ => throw new Xunit.Sdk.XunitException("Backend must not be called."));

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetVideoOperationStatus("nmv1_not-base64!"));

        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static NetMindProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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
