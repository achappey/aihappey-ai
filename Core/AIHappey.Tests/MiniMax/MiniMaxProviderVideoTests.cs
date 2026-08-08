using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.MiniMax;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.MiniMax;

public sealed class MiniMaxProviderVideoTests
{
    [Fact]
    public async Task Start_and_status_preserve_model_in_url_safe_operation()
    {
        const string taskId = "428551194128598";
        const string model = "T2V-01-Director";
        var requestCount = 0;

        var provider = CreateProvider(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/v1/video_generation", request.RequestUri?.AbsolutePath);
                return JsonResponse(new { task_id = taskId, base_resp = new { status_code = 0 } });
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/query/video_generation", request.RequestUri?.AbsolutePath);
            Assert.Equal($"?task_id={taskId}", request.RequestUri?.Query);
            return JsonResponse(new { status = "Processing", base_resp = new { status_code = 0 } });
        });

        var start = await provider.StartVideoOperation(new VideoRequest { Model = model, Prompt = "A cat" });

        Assert.StartsWith("mmv1_", start.Operation, StringComparison.Ordinal);
        Assert.DoesNotContain('/', start.Operation);
        Assert.DoesNotContain('+', start.Operation);
        Assert.DoesNotContain('=', start.Operation);

        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(start.Operation));
        Assert.Equal($"minimax/{model}", pending.Response.ModelId);
    }

    [Fact]
    public async Task Legacy_raw_task_id_remains_pollable()
    {
        const string taskId = "legacy task/id";
        var provider = CreateProvider(request =>
        {
            Assert.Equal($"?task_id={Uri.EscapeDataString(taskId)}", request.RequestUri?.Query);
            return JsonResponse(new { status = "Processing", base_resp = new { status_code = 0 } });
        });

        var pending = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(Uri.EscapeDataString(taskId)));

        Assert.Equal("minimax", pending.Response.ModelId);
    }

    [Fact]
    public async Task Invalid_operation_envelope_is_rejected_before_polling()
    {
        var provider = CreateProvider(_ => throw new Xunit.Sdk.XunitException("Backend must not be called."));

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetVideoOperationStatus("mmv1_not-base64!"));

        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MiniMaxProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
