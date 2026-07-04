using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.DeepInfra;

public sealed class DeepInfraProviderVideoTests
{
    [Fact]
    public async Task VideoRequest_text_to_video_posts_payload_polls_and_downloads_video()
    {
        var requestJson = string.Empty;
        var pollCount = 0;
        var videoBytes = Encoding.UTF8.GetBytes("deepinfra-video-bytes");

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/videos")
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return JsonResponse("""
                    {"id":"job_123","object":"video.generation.job","created_at":1774344160,"status":"queued","model":"deepinfra-video-model"}
                    """);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v1/videos/job_123")
            {
                pollCount++;
                return JsonResponse(pollCount == 1
                    ? """
                      {"id":"job_123","status":"running","model":"deepinfra-video-model"}
                      """
                    : """
                      {"id":"job_123","status":"completed","model":"deepinfra-video-model","data":[{"url":"https://cdn.example.com/output.mp4","format":"mp4"}]}
                      """);
            }

            if (string.Equals(request.RequestUri?.AbsoluteUri, "https://cdn.example.com/output.mp4", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(videoBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return Unexpected(request);
        });

        var result = await provider.VideoRequest(new VideoRequest
        {
            Model = "deepinfra-video-model",
            Prompt = "A cinematic shot of a robot walking through Amsterdam",
            AspectRatio = "16:9",
            Resolution = "1280x720",
            Duration = 6,
            Seed = 42,
            Fps = 24,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["deepinfra"] = JsonSerializer.SerializeToElement(new
                {
                    negative_prompt = "blurry",
                    style = "cinematic",
                    poll_interval_seconds = 1,
                    poll_timeout_minutes = 1
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);
        Assert.Equal("deepinfra-video-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("A cinematic shot of a robot walking through Amsterdam", payload.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("blurry", payload.RootElement.GetProperty("negative_prompt").GetString());
        Assert.Equal("16:9", payload.RootElement.GetProperty("aspect_ratio").GetString());
        Assert.Equal("1280x720", payload.RootElement.GetProperty("size").GetString());
        Assert.Equal(6, payload.RootElement.GetProperty("seconds").GetInt32());
        Assert.Equal(42, payload.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal("cinematic", payload.RootElement.GetProperty("style").GetString());

        var video = Assert.Single(result.Videos ?? []);
        Assert.Equal(Convert.ToBase64String(videoBytes), video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Contains(result.Warnings, warning => warning.ToString()?.Contains("fps", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task VideoRequest_with_image_maps_first_frame_to_image_url_data_uri()
    {
        var requestJson = string.Empty;

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/videos")
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                return JsonResponse("""
                    {"id":"job_i2v","status":"completed","model":"deepinfra-video-model","data":[{"data":"ZGVlcGluZnJhLXZpZGVv","mime_type":"video/mp4"}]}
                    """);
            }

            return Unexpected(request);
        });

        var result = await provider.VideoRequest(new VideoRequest
        {
            Model = "deepinfra-video-model",
            Prompt = "Animate the first frame",
            Image = new VideoFile
            {
                MediaType = MediaTypeNames.Image.Png,
                Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("first-frame"))
            }
        });

        using var payload = JsonDocument.Parse(requestJson);
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes("first-frame"))}",
            payload.RootElement.GetProperty("image_url").GetString());

        var video = Assert.Single(result.Videos ?? []);
        Assert.Equal("ZGVlcGluZnJhLXZpZGVv", video.Data);
        Assert.Equal("video/mp4", video.MediaType);
    }

    [Fact]
    public async Task VideoRequest_failed_status_throws_provider_error()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/videos")
            {
                return JsonResponse("""
                    {"id":"job_failed","status":"failed","model":"deepinfra-video-model","error":"quota exceeded"}
                    """);
            }

            return Unexpected(request);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.VideoRequest(new VideoRequest
        {
            Model = "deepinfra-video-model",
            Prompt = "A failing generation"
        }));

        Assert.Contains("quota exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListModels_classifies_video_models()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v1/models")
            {
                return JsonResponse("""
                    {
                      "data": [
                        {"id":"deepinfra-video-model","owned_by":"deepinfra"},
                        {"id":"black-forest-labs/FLUX.1-schnell","owned_by":"deepinfra"}
                      ]
                    }
                    """);
            }

            return Unexpected(request);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Contains(models, model => model.Name == "deepinfra-video-model" && model.Type == "video");
        Assert.Contains(models, model => model.Name == "black-forest-labs/FLUX.1-schnell" && model.Type == "image");
    }

    private static DeepInfraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new DeepInfraProvider(new StaticApiKeyResolver(), httpClientFactory, cache);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpResponseMessage Unexpected(HttpRequestMessage request)
        => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected request: {request.Method} {request.RequestUri}")
        };

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
