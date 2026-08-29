using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Venice;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Venice;

public sealed class VeniceProviderVideoTests
{
    [Fact]
    public async Task Queue_model_is_preserved_in_opaque_token_and_pending_status()
    {
        var requestCount = 0;
        var provider = CreateProvider(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.Equal("/api/v1/video/queue", request.RequestUri?.AbsolutePath);
                return JsonResponse(new { model = "venice-private-model", queue_id = "queue-1" });
            }

            Assert.Equal("/api/v1/video/retrieve", request.RequestUri?.AbsolutePath);
            var payload = ReadJson(request);
            Assert.Equal("venice-private-model", payload.GetProperty("model").GetString());
            Assert.Equal("queue-1", payload.GetProperty("queue_id").GetString());
            Assert.False(payload.GetProperty("delete_media_on_completion").GetBoolean());
            return JsonResponse(new { status = "PROCESSING", average_execution_time = 1000, execution_duration = 100 });
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "requested-model",
            Prompt = "A gondola",
            Duration = 5
        });
        Assert.StartsWith("vnv1_", started.Operation, StringComparison.Ordinal);
        Assert.Equal("venice/venice-private-model", started.Response.ModelId);

        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(started.Operation));
        Assert.Equal("venice/venice-private-model", pending.Response.ModelId);
    }

    [Fact]
    public async Task Private_video_is_downloaded_before_complete_and_cleanup_failure_is_metadata()
    {
        var calls = new List<string>();
        var provider = CreateProvider(request =>
        {
            calls.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return calls.Count switch
            {
                1 => JsonResponse(new
                {
                    model = "private-model",
                    queue_id = "queue-2",
                    download_url = "https://download.example/video.mp4"
                }),
                2 => JsonResponse(new { status = "COMPLETED", average_execution_time = 1000, execution_duration = 900 }),
                3 => VideoResponse([1, 2, 3]),
                4 => JsonResponse(new { message = "cleanup unavailable" }, HttpStatusCode.InternalServerError),
                _ => throw new Xunit.Sdk.XunitException("Unexpected request.")
            };
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "requested-model",
            Prompt = "A gondola",
            Duration = 5
        });
        var completed = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal([
            "/api/v1/video/queue",
            "/api/v1/video/retrieve",
            "/video.mp4",
            "/api/v1/video/complete"
        ], calls);
        Assert.Equal("venice/private-model", completed.Response.ModelId);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), Assert.Single(completed.Videos).Data);
        var metadata = Assert.Contains("venice", completed.ProviderMetadata ?? []);
        Assert.False(metadata.GetProperty("cleanup").GetProperty("success").GetBoolean());
        Assert.Contains("cleanup unavailable", metadata.GetProperty("cleanup").GetProperty("error").GetString());
    }

    [Fact]
    public async Task Direct_video_is_completed_only_after_bytes_are_received()
    {
        var calls = new List<string>();
        var provider = CreateProvider(request =>
        {
            calls.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return calls.Count switch
            {
                1 => JsonResponse(new { model = "direct-model", queue_id = "queue-3" }),
                2 => VideoResponse([4, 5, 6]),
                3 => JsonResponse(new { success = true }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected request.")
            };
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "direct-model",
            Prompt = "A gondola",
            Duration = 5
        });
        var completed = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("venice/direct-model", completed.Response.ModelId);
        Assert.Equal("video/mp4", Assert.Single(completed.Videos).MediaType);
        Assert.Equal("/api/v1/video/complete", calls[^1]);
        Assert.True(Assert.Contains("venice", completed.ProviderMetadata ?? [])
            .GetProperty("cleanup").GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Invalid_or_legacy_operation_is_rejected_before_polling()
    {
        var provider = CreateProvider(_ => throw new Xunit.Sdk.XunitException("Backend must not be called."));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("legacy-queue-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("vnv1_not-base64!"));
    }

    private static VeniceProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static JsonElement ReadJson(HttpRequestMessage request)
    {
        var raw = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()
            ?? throw new Xunit.Sdk.XunitException("Expected JSON content.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage VideoResponse(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("video/mp4");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
