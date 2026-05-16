using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AgnesAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AgnesAI;

public sealed class AgnesAIProviderMediaTests
{
    [Fact]
    public async Task ImageRequest_text_to_image_posts_to_generations_and_downloads_image()
    {
        var requestJson = string.Empty;
        var imageBytes = Encoding.UTF8.GetBytes("agnes-image-bytes");

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/images/generations")
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"created":1774432125,"data":[{"url":"https://cdn.example.com/generated.png"}],"usage":{"generated_images":1}}
                        """,
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };
            }

            if (string.Equals(request.RequestUri?.AbsoluteUri, "https://cdn.example.com/generated.png", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(imageBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Image.Png);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return Unexpected(request);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "agnes-image-1.2",
            Prompt = "A futuristic city skyline at sunset",
            Size = "1024x768"
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("agnes-image-1.2", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("A futuristic city skyline at sunset", payload.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("1024x768", payload.RootElement.GetProperty("size").GetString());
        Assert.Equal("url", payload.RootElement.GetProperty("extra_body").GetProperty("response_format").GetString());

        var image = Assert.Single(result.Images ?? []);
        Assert.Equal(Convert.ToBase64String(imageBytes).ToDataUrl(MediaTypeNames.Image.Png), image);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ImageRequest_with_reference_urls_uses_img2img_and_warns_for_ignored_local_files()
    {
        var requestJson = string.Empty;
        var imageBytes = Encoding.UTF8.GetBytes("agnes-edit-image");

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/images/generations")
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"created":1774432125,"data":[{"url":"https://cdn.example.com/edit.png"}]}
                        """,
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };
            }

            if (string.Equals(request.RequestUri?.AbsoluteUri, "https://cdn.example.com/edit.png", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(imageBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Image.Png);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return Unexpected(request);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "agnes-image-2.0-flash",
            Prompt = "Combine the two characters into a fight scene",
            Files =
            [
                new ImageFile
                {
                    MediaType = MediaTypeNames.Image.Png,
                    Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("local-file"))
                }
            ],
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["agnesai"] = JsonSerializer.SerializeToElement(new
                {
                    image_urls = new[]
                    {
                        "https://example.com/image1.png",
                        "https://example.com/image2.png"
                    }
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("img2img", payload.RootElement.GetProperty("tags")[0].GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("extra_body").GetProperty("image").GetArrayLength());
        Assert.Contains(result.Warnings, warning => warning.ToString()?.Contains("files", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Single(result.Images ?? []);
    }

    [Fact]
    public async Task ImageRequest_missing_output_url_throws()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/images/generations")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"created":1774432125,"data":[{}]}
                        """,
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };
            }

            return Unexpected(request);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "agnes-image-1.2",
            Prompt = "A futuristic city skyline at sunset"
        }));

        Assert.Contains("data[].url", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VideoRequest_text_to_video_polls_until_completed_and_downloads_video()
    {
        var requestJson = string.Empty;
        var pollCount = 0;
        var videoBytes = Encoding.UTF8.GetBytes("agnes-video-bytes");

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/videos")
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"id":"task_123456","object":"video","model":"agnes-video-v2.0","status":"queued","progress":0,"created_at":1774344160}
                        """,
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v1/videos/task_123456")
            {
                pollCount++;

                var json = pollCount == 1
                    ? """
                      {"id":"task_123456","status":"in_progress","progress":25}
                      """
                    : """
                      {"id":"task_123456","status":"completed","progress":100,"video_url":"https://cdn.example.com/output.mp4","size":"1152x768","seconds":"5.0"}
                      """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
                };
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
            Model = "agnes-video-v2.0",
            Prompt = "A cinematic shot of a cat walking on the beach at sunset",
            Resolution = "1152x768",
            Fps = 24,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["agnesai"] = JsonSerializer.SerializeToElement(new
                {
                    num_frames = 121,
                    negative_prompt = "blurry"
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal(1152, payload.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(768, payload.RootElement.GetProperty("height").GetInt32());
        Assert.Equal(24, payload.RootElement.GetProperty("frame_rate").GetInt32());
        Assert.Equal(121, payload.RootElement.GetProperty("num_frames").GetInt32());
        Assert.Equal("blurry", payload.RootElement.GetProperty("negative_prompt").GetString());

        var video = Assert.Single(result.Videos ?? []);
        Assert.Equal(Convert.ToBase64String(videoBytes), video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task VideoRequest_with_keyframe_urls_uses_extra_body_and_warns_for_ignored_local_image()
    {
        var requestJson = string.Empty;
        var videoBytes = Encoding.UTF8.GetBytes("agnes-keyframes");

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.PathAndQuery == "/v1/videos")
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"id":"task_keyframes","status":"queued"}
                        """,
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v1/videos/task_keyframes")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"id":"task_keyframes","status":"completed","progress":100,"video_url":"https://cdn.example.com/keyframes.mp4"}
                        """,
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };
            }

            if (string.Equals(request.RequestUri?.AbsoluteUri, "https://cdn.example.com/keyframes.mp4", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(videoBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return Unexpected(request);
        });

        var result = await provider.VideoRequest(new VideoRequest
        {
            Model = "agnes-video-v1.2",
            Prompt = "Smooth transition between scenes",
            Image = new VideoFile
            {
                MediaType = MediaTypeNames.Image.Png,
                Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("local-image"))
            },
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["agnesai"] = JsonSerializer.SerializeToElement(new
                {
                    image_urls = new[]
                    {
                        "https://example.com/keyframe1.png",
                        "https://example.com/keyframe2.png"
                    },
                    mode = "keyframes",
                    num_frames = 121
                }, JsonSerializerOptions.Web)
            }
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("keyframes", payload.RootElement.GetProperty("extra_body").GetProperty("mode").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("extra_body").GetProperty("image").GetArrayLength());
        Assert.Contains(result.Warnings, warning => warning.ToString()?.Contains("image", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Single(result.Videos ?? []);
    }

    [Fact]
    public async Task ListModels_classifies_image_and_video_models_correctly()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v1/models")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "data": [
                            {"id": "agnes-image-2.0-flash", "owned_by": "agnes"},
                            {"id": "agnes-video-v2.0", "owned_by": "agnes"},
                            {"id": "agnes-1.5-pro", "owned_by": "agnes"}
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };
            }

            return Unexpected(request);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Contains(models, model => model.Name == "agnes-image-2.0-flash" && model.Type == "image");
        Assert.Contains(models, model => model.Name == "agnes-video-v2.0" && model.Type == "video");
        Assert.Contains(models, model => model.Name == "agnes-1.5-pro" && model.Type == "language");
    }

    private static AgnesAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new AgnesAIProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

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
