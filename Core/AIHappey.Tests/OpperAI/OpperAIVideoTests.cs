using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpperAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpperAI;

public sealed class OpperAIVideoTests
{
    [Fact]
    public async Task StartVideoOperation_flattens_provider_metadata_forces_store_false_and_preserves_model()
    {
        string? createBody = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v3/videos", request.RequestUri?.AbsolutePath);
            createBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.Accepted, """{"id":"artifact-1","status_url":"https://api.opper.ai/v3/artifacts/artifact-1/status"}""");
        });

        var result = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "google/veo-3.1",
            Prompt = "A fox in snow",
            Duration = 8,
            ProviderOptions = new()
            {
                ["opperai"] = JsonSerializer.SerializeToElement(new
                {
                    parameters = new { negative_prompt = "blur" },
                    custom_field = "passthrough",
                    model = "wrong-model",
                    prompt = "wrong prompt",
                    store = true
                })
            }
        });

        using var document = JsonDocument.Parse(Assert.IsType<string>(createBody));
        var root = document.RootElement;
        Assert.Equal("google/veo-3.1", root.GetProperty("model").GetString());
        Assert.Equal("A fox in snow", root.GetProperty("prompt").GetString());
        Assert.Equal(8, root.GetProperty("seconds").GetInt32());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("passthrough", root.GetProperty("custom_field").GetString());
        Assert.Equal("blur", root.GetProperty("parameters").GetProperty("negative_prompt").GetString());
        Assert.StartsWith("opv1_", result.Operation);
        Assert.Equal("opperai/google/veo-3.1", result.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_uses_opaque_status_url_and_original_model()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            if (calls == 1)
                return JsonResponse(HttpStatusCode.Accepted, """{"id":"artifact-2","status_url":"https://status.example/artifact-2"}""");

            Assert.Equal("https://status.example/artifact-2", request.RequestUri?.ToString());
            return JsonResponse(HttpStatusCode.OK, """{"id":"artifact-2","status":"processing"}""");
        });

        var started = await provider.StartVideoOperation(new VideoRequest { Model = "minimax/video-01", Prompt = "Clouds" });
        var status = Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal("opperai/minimax/video-01", status.Response.ModelId);
    }

    [Fact]
    public async Task GetVideoOperationStatus_downloads_completed_video()
    {
        var calls = 0;
        var video = new byte[] { 1, 2, 3, 4 };
        var provider = CreateProvider(request =>
        {
            calls++;
            return calls switch
            {
                1 => JsonResponse(HttpStatusCode.Accepted, """{"id":"artifact-3","status_url":"https://status.example/artifact-3"}"""),
                2 => JsonResponse(HttpStatusCode.OK, """{"id":"artifact-3","status":"completed","url":"https://cdn.example/video.mp4"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(video)
                    {
                        Headers = { ContentType = new("video/mp4") }
                    }
                }
            };
        });

        var started = await provider.StartVideoOperation(new VideoRequest { Model = "provider/model", Prompt = "Ocean" });
        var completed = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus(started.Operation));
        var output = Assert.Single(completed.Videos);
        Assert.Equal("base64", output.Type);
        Assert.Equal("video/mp4", output.MediaType);
        Assert.Equal(Convert.ToBase64String(video), output.Data);
        Assert.Equal("completed", completed.ProviderMetadata!["opperai"].GetProperty("status").GetString());
    }

    private static OpperAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
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

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
