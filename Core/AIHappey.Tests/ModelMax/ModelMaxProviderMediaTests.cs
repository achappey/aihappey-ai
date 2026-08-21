using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ModelMax;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ModelMax;

public sealed class ModelMaxProviderMediaTests
{
    [Fact]
    public async Task Image_generation_raw_passes_options_and_maps_base64()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/images/generations", request.RequestUri?.AbsolutePath);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"created":1709123456,"data":[{"b64_json":"pixels"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}""");
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "imagen-3",
            Prompt = "city",
            N = 2,
            Size = "1024x1024",
            ProviderOptions = new() { ["modelmax"] = JsonSerializer.SerializeToElement(new { response_format = "b64_json", quality = "hd", custom = 7 }) }
        });

        using var json = JsonDocument.Parse(body!);
        Assert.Equal("imagen-3", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("hd", json.RootElement.GetProperty("quality").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("custom").GetInt32());
        Assert.Equal("data:image/png;base64,pixels", Assert.Single(result.Images!));
        Assert.Equal(5, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task Image_generation_downloads_url_and_rejects_edits()
    {
        var bytes = Encoding.UTF8.GetBytes("webp");
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"data":[{"url":"https://cdn.example/image.webp"}]}""");
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await provider.ImageRequest(new ImageRequest { Model = "imagen-3", Prompt = "city" });
        Assert.Equal($"data:image/webp;base64,{Convert.ToBase64String(bytes)}", Assert.Single(result.Images!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "imagen-3", Prompt = "edit", Files = [new ImageFile { MediaType = "image/png", Data = "x" }]
        }));
    }

    [Fact]
    public async Task Video_operation_preserves_exact_model_for_status_result_and_content()
    {
        const string model = "veo-3-fast";
        const string requestId = "req/id+1";
        var call = 0;
        var provider = CreateProvider(request =>
        {
            call++;
            if (call == 1)
            {
                Assert.Equal("/v1/queue/veo-3-fast", request.RequestUri?.AbsolutePath);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var json = JsonDocument.Parse(body);
                Assert.Equal("keep", json.RootElement.GetProperty("custom").GetString());
                var parameters = json.RootElement.GetProperty("parameters");
                Assert.Equal("4k", parameters.GetProperty("resolution").GetString());
                Assert.False(parameters.GetProperty("generate_audio").GetBoolean());
                Assert.Equal("avoid rain", parameters.GetProperty("negative_prompt").GetString());
                return JsonResponse($$"""{"request_id":"{{requestId}}","status":"IN_QUEUE"}""");
            }
            if (call == 2)
            {
                Assert.Equal($"/v1/queue/{model}/requests/{Uri.EscapeDataString(requestId)}/status", request.RequestUri?.AbsolutePath);
                return JsonResponse($$"""{"request_id":"{{requestId}}","status":"COMPLETED"}""");
            }
            if (call == 3)
            {
                Assert.Equal($"/v1/queue/{model}/requests/{Uri.EscapeDataString(requestId)}", request.RequestUri?.AbsolutePath);
                return JsonResponse($$"""{"request_id":"{{requestId}}","status":"COMPLETED","model":"{{model}}","data":[{"url":"/v1/queue/{{model}}/requests/content-id/content/0"}]}""");
            }

            Assert.Equal($"/v1/queue/{model}/requests/content-id/content/0", request.RequestUri?.AbsolutePath);
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = model,
            Prompt = "reef",
            Resolution = "4k",
            GenerateAudio = false,
            ProviderOptions = new() { ["modelmax"] = JsonSerializer.SerializeToElement(new { custom = "keep", parameters = new { negative_prompt = "avoid rain" } }) }
        });
        Assert.StartsWith("mxv1_", started.Operation, StringComparison.Ordinal);
        Assert.DoesNotContain('/', started.Operation);

        var completed = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus(started.Operation));
        Assert.Equal($"modelmax/{model}", completed.Response.ModelId);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), Assert.Single(completed.Videos).Data);
    }

    [Fact]
    public async Task Video_pending_uses_model_path_and_bad_tokens_are_rejected()
    {
        var call = 0;
        var provider = CreateProvider(request =>
        {
            call++;
            return call == 1
                ? JsonResponse("""{"request_id":"request-1","status":"IN_QUEUE"}""")
                : JsonResponse("""{"request_id":"request-1","status":"IN_PROGRESS"}""");
        });

        var started = await provider.StartVideoOperation(new VideoRequest { Model = "veo-3", Prompt = "cat" });
        var pending = Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus(started.Operation));
        Assert.Equal("modelmax/veo-3", pending.Response.ModelId);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("mxv1_not-base64!"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("legacy-request-id"));
    }

    private static ModelMaxProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new StaticHttpClientFactory(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json) };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticResponseHttpMessageHandler(responder));
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
