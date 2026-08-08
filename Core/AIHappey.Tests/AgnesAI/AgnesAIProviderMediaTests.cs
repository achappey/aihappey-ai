using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AgnesAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AgnesAI;

public sealed class AgnesAIProviderMediaTests
{
    [Fact]
    public async Task ImageRequest_prefers_base64_and_maps_size_ratio()
    {
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"image-base64"}]}""");
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "agnes-image-2.1-flash",
            Prompt = "dense city",
            Size = "2K",
            AspectRatio = "16:9"
        });

        using var payload = JsonDocument.Parse(requestJson!);
        Assert.Equal("2K", payload.RootElement.GetProperty("size").GetString());
        Assert.Equal("16:9", payload.RootElement.GetProperty("ratio").GetString());
        Assert.True(payload.RootElement.GetProperty("return_base64").GetBoolean());
        Assert.Equal("b64_json", payload.RootElement.GetProperty("extra_body").GetProperty("response_format").GetString());
        Assert.False(payload.RootElement.TryGetProperty("tags", out _));
        Assert.Equal("data:image/png;base64,image-base64", Assert.Single(result.Images!));
    }

    [Fact]
    public async Task ImageRequest_edit_converts_upload_to_data_uri_without_tags()
    {
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"edited"}]}""");
        });

        await provider.ImageRequest(new ImageRequest
        {
            Model = "agnes-image-2.0-flash",
            Prompt = "make it orange",
            Size = "1024x768",
            Files = [new ImageFile { MediaType = "image/png", Data = "raw-base64" }]
        });

        using var payload = JsonDocument.Parse(requestJson!);
        Assert.Equal("data:image/png;base64,raw-base64",
            payload.RootElement.GetProperty("extra_body").GetProperty("image")[0].GetString());
        Assert.False(payload.RootElement.TryGetProperty("tags", out _));
    }

    [Fact]
    public async Task ImageRequest_downloads_url_when_base64_is_unavailable()
    {
        var bytes = Encoding.UTF8.GetBytes("image-bytes");
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"data":[{"url":"https://cdn.example.com/image.webp"}]}""");

            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "agnes-image-2.0-flash",
            Prompt = "fallback"
        });

        Assert.Equal($"data:image/webp;base64,{Convert.ToBase64String(bytes)}", Assert.Single(result.Images!));
    }

    [Fact]
    public async Task StartAndStatusVideo_use_opaque_model_preserving_video_id_operation()
    {
        string? createJson = null;
        string? pollPath = null;
        var bytes = Encoding.UTF8.GetBytes("video-bytes");
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                createJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse("""{"id":"task_1","task_id":"task_1","video_id":"video_1","status":"queued"}""");
            }

            if (request.RequestUri?.AbsoluteUri == "https://cdn.example.com/video.mp4")
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            pollPath = request.RequestUri?.PathAndQuery;
            return JsonResponse("""{"task_id":"task_1","video_id":"video_1","model":"agnes-video-v2.0","status":"completed","progress":100,"metadata":{"url":"https://cdn.example.com/video.mp4"}}""");
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "agnes-video-v2.0",
            Prompt = "cat on beach",
            Resolution = "1152x768",
            Fps = 24,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["agnesai"] = JsonSerializer.SerializeToElement(new { num_frames = 121 })
            }
        });

        Assert.StartsWith("agv1_", started.Operation, StringComparison.Ordinal);
        Assert.Equal("agnesai/agnes-video-v2.0", started.Response.ModelId);
        using (var payload = JsonDocument.Parse(createJson!))
        {
            Assert.Equal(121, payload.RootElement.GetProperty("num_frames").GetInt32());
            Assert.Equal(24, payload.RootElement.GetProperty("frame_rate").GetInt32());
        }

        var completed = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(started.Operation));
        Assert.Equal("/agnesapi?video_id=video_1&model_name=agnes-video-v2.0", pollPath);
        Assert.Equal("agnesai/agnes-video-v2.0", completed.Response.ModelId);
        Assert.Equal(Convert.ToBase64String(bytes), Assert.Single(completed.Videos).Data);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public async Task VideoStatus_non_terminal_is_pending(string nativeStatus)
    {
        var provider = CreateProvider(_ => JsonResponse($$"""{"video_id":"video_raw","status":"{{nativeStatus}}","progress":20}"""));
        var result = await provider.GetVideoOperationStatus("video_raw");
        var pending = Assert.IsType<VideoOperationPendingResult>(result);
        Assert.Equal("agnesai", pending.Response.ModelId);
    }

    [Fact]
    public async Task VideoStatus_failed_is_error()
    {
        var provider = CreateProvider(_ => JsonResponse("""{"video_id":"video_raw","status":"failed","error":{"message":"busy"}}"""));
        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("video_raw"));
        Assert.Contains("busy", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyVideoRequest_is_unsupported()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP must not be called"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.VideoRequest(new VideoRequest()));
    }

    [Fact]
    public async Task OpenAI_generation_and_mimic_stream_use_core_image_path()
    {
        var provider = CreateProvider(_ => JsonResponse("""{"data":[{"b64_json":"generated"}]}"""));
        var options = new OpenAIImageGenerationRequest
        {
            Model = "agnes-image-2.1-flash",
            Prompt = "generate"
        };

        var response = await provider.OpenAIImageGenerationRequestAsync(options);
        Assert.Equal("generated", Assert.Single(response.Data!).B64Json);

        var events = new List<IOpenAIImageStreamEvent>();
        await foreach (var item in provider.OpenAIImageGenerationStreamingAsync(options))
            events.Add(item);
        Assert.Single(events);
        Assert.IsType<OpenAIImageGenerationCompleted>(events[0]);
    }

    [Fact]
    public async Task OpenAI_edit_and_mimic_stream_convert_form_upload()
    {
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"edited"}]}""");
        });
        var options = new OpenAIImageEditRequest
        {
            Model = "agnes-image-2.1-flash",
            Prompt = "edit",
            ImageFiles = [FormImage()]
        };

        var response = await provider.OpenAIImageEditRequestAsync(options);
        Assert.Equal("edited", Assert.Single(response.Data!).B64Json);
        Assert.Contains("data:image/png;base64,", requestJson, StringComparison.Ordinal);

        var events = new List<IOpenAIImageStreamEvent>();
        await foreach (var item in provider.OpenAIImageEditStreamingAsync(options))
            events.Add(item);
        Assert.Single(events);
        Assert.IsType<OpenAIImageEditCompleted>(events[0]);
    }

    private static AgnesAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static IFormFile FormImage()
    {
        var bytes = new byte[] { 1, 2, 3 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "image.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
