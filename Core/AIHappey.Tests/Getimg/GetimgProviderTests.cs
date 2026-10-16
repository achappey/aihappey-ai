using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Getimg;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Getimg;

public sealed class GetimgProviderTests
{
    [Fact]
    public async Task ListModels_maps_catalog_types_with_provider_prefix()
    {
        string? path = null;
        string? auth = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            auth = request.Headers.Authorization?.ToString();
            return JsonResponse(new object[]
            {
                new { id = "seedream-5-lite", name = "Seedream 5 Lite", type = "image", supported_references = new { reference_image = 14 } },
                new { id = "wan-3-0", name = "Wan 3.0", type = "video", supports_sound = true }
            });
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Equal("/v2/models", path);
        Assert.Equal("Bearer test-api-key", auth);
        Assert.Collection(models,
            m => { Assert.Equal("getimg/seedream-5-lite", m.Id); Assert.Equal("image", m.Type); Assert.Contains("reference_image", m.Tags!); },
            m => { Assert.Equal("getimg/wan-3-0", m.Id); Assert.Equal("video", m.Type); Assert.Contains("audio", m.Tags!); });
    }

    [Fact]
    public async Task ImageRequest_posts_prompt_and_passthrough_options_and_downloads_output()
    {
        const string outputUrl = "https://cdn.example.com/output.png";
        var imageBytes = Encoding.UTF8.GetBytes("image");
        string? body = null;
        string? downloadAuth = "unset";

        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v2/images/generations")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("Bearer test-api-key", request.Headers.Authorization?.ToString());
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new { id = "req-1", status = "completed", data = new[] { new { url = outputUrl } }, usage = new { total_cost = 0.03 } });
            }

            Assert.Equal(outputUrl, request.RequestUri?.ToString());
            downloadAuth = request.Headers.Authorization?.ToString();
            var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Image.Png);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "getimg/seedream-5-lite",
            Prompt = "A lion",
            AspectRatio = "16:9",
            ProviderOptions = new() { ["getimg"] = JsonSerializer.SerializeToElement(new { output_format = "png" }) }
        });

        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("seedream-5-lite", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("A lion", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("16:9", doc.RootElement.GetProperty("aspect_ratio").GetString());
        Assert.Equal("png", doc.RootElement.GetProperty("output_format").GetString());
        Assert.Null(downloadAuth);
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(imageBytes)}", Assert.Single(response.Images!));
        Assert.Equal("getimg/seedream-5-lite", response.Response.ModelId);
    }

    [Fact]
    public async Task Video_operation_submits_then_polls_until_completed()
    {
        var videoBytes = Encoding.UTF8.GetBytes("video");
        string? submitBody = null;
        var polls = 0;

        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/v2/videos/generations")
            {
                submitBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new { id = "req-video", status = "pending" });
            }

            if (path == "/v2/videos/generations/req-video")
            {
                polls++;
                return polls == 1
                    ? JsonResponse(new { id = "req-video", status = "pending", model = "wan-3-0" })
                    : JsonResponse(new { id = "req-video", status = "completed", model = "wan-3-0", data = new[] { new { url = "https://api.getimg.ai/v2/downloads/req-video/0", mime_type = "video/mp4" } } });
            }

            Assert.Equal("/v2/downloads/req-video/0", path);
            Assert.Equal("Bearer test-api-key", request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(videoBytes) };
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "wan-3-0",
            Prompt = "Skate shoes",
            AspectRatio = "16:9",
            Resolution = "480p",
            Duration = 4,
            GenerateAudio = true,
            Image = new VideoFileUrl { Url = "https://example.com/first.png" }
        });

        Assert.Equal("req-video", started.Operation);
        using (var doc = JsonDocument.Parse(submitBody!))
        {
            Assert.Equal("wan-3-0", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("480p", doc.RootElement.GetProperty("resolution").GetString());
            Assert.Equal(4, doc.RootElement.GetProperty("duration").GetInt32());
            Assert.True(doc.RootElement.GetProperty("sound").GetBoolean());
            var image = Assert.Single(doc.RootElement.GetProperty("images").EnumerateArray());
            Assert.Equal("first_frame", image.GetProperty("role").GetString());
        }

        Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus(started.Operation));
        var completed = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus(started.Operation));
        var video = Assert.Single(completed.Videos);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(Convert.ToBase64String(videoBytes), video.Data);
        Assert.Equal("getimg/wan-3-0", completed.Response.ModelId);
    }

    private static GetimgProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder)));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new GetimgProvider(new StaticApiKeyResolver(), cache, factory);
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

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
