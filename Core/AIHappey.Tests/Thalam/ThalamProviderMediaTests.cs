using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Thalam;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Thalam;

public sealed class ThalamProviderMediaTests
{
    [Fact]
    public async Task ImageRequest_merges_provider_options_and_downloads_urls()
    {
        HttpRequestMessage? capturedCreate = null;
        var imageBytes = Encoding.UTF8.GetBytes("png-bytes");
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/images/generations")
            {
                capturedCreate = CloneRequest(request);
                return JsonResponse(new
                {
                    created = 1736294400,
                    data = new[] { new { url = "https://cdn.thalam.test/output.png" } }
                });
            }

            if (request.RequestUri?.AbsoluteUri == "https://cdn.thalam.test/output.png")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes)
                };
                response.Content.Headers.ContentType = new(MediaTypeNames.Image.Png);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "bytedance/seedream-4.0",
            Prompt = "A city skyline",
            Size = "2048x2048",
            N = 1,
            ProviderOptions = ProviderOptions(new
            {
                aspect_ratio = "16:9",
                user = "client-supplied"
            })
        });

        Assert.NotNull(capturedCreate);
        using var payloadDocument = JsonDocument.Parse(await capturedCreate!.Content!.ReadAsStringAsync());
        var payload = payloadDocument.RootElement;
        Assert.Equal("bytedance/seedream-4.0", payload.GetProperty("model").GetString());
        Assert.Equal("A city skyline", payload.GetProperty("prompt").GetString());
        Assert.Equal("2048x2048", payload.GetProperty("size").GetString());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("client-supplied", payload.GetProperty("user").GetString());

        var image = Assert.Single(response.Images ?? []);
        Assert.Equal(Convert.ToBase64String(imageBytes).ToDataUrl(MediaTypeNames.Image.Png), image);
        Assert.True(response.ProviderMetadata?.ContainsKey("thalam"));
        Assert.Equal("thalam/bytedance/seedream-4.0", response.Response.ModelId);
    }

    [Fact]
    public async Task SpeechRequest_posts_openai_compatible_payload_and_returns_raw_mp3()
    {
        HttpRequestMessage? capturedSpeech = null;
        var audioBytes = Encoding.UTF8.GetBytes("mp3-bytes");
        var provider = CreateProvider(request =>
        {
            capturedSpeech = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioBytes)
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "minimax/minimax-speech-2.8-hd",
            Text = "Welcome to Thalam.",
            Voice = "English_Graceful_Lady",
            ProviderOptions = ProviderOptions(new { emotion = "happy" })
        });

        Assert.NotNull(capturedSpeech);
        Assert.Equal(HttpMethod.Post, capturedSpeech!.Method);
        Assert.Equal("/v1/audio/speech", capturedSpeech.RequestUri?.AbsolutePath);

        using var payloadDocument = JsonDocument.Parse(await capturedSpeech.Content!.ReadAsStringAsync());
        var payload = payloadDocument.RootElement;
        Assert.Equal("minimax/minimax-speech-2.8-hd", payload.GetProperty("model").GetString());
        Assert.Equal("Welcome to Thalam.", payload.GetProperty("input").GetString());
        Assert.Equal("English_Graceful_Lady", payload.GetProperty("voice").GetString());
        Assert.Equal("happy", payload.GetProperty("emotion").GetString());

        Assert.Equal(Convert.ToBase64String(audioBytes), response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("mp3", response.Audio.Format);
    }

    [Fact]
    public async Task VideoRequest_submits_polls_and_downloads_video()
    {
        HttpRequestMessage? capturedCreate = null;
        var pollCount = 0;
        var videoBytes = Encoding.UTF8.GetBytes("mp4-bytes");
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/videos/generations")
            {
                capturedCreate = CloneRequest(request);
                return JsonResponse(new { task_id = "task-123", status = "processing" });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/videos/tasks/task-123")
            {
                pollCount++;
                return JsonResponse(pollCount == 1
                    ? new
                    {
                        task = new { task_id = "task-123", status = "TASK_STATUS_PROCESSING", reason = "" },
                        videos = Array.Empty<object>()
                    }
                    : new
                    {
                        task = new { task_id = "task-123", status = "TASK_STATUS_SUCCEED", reason = "" },
                        videos = new[] { new { video_url = "https://cdn.thalam.test/output.mp4" } }
                    });
            }

            if (request.RequestUri?.AbsoluteUri == "https://cdn.thalam.test/output.mp4")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(videoBytes)
                };
                response.Content.Headers.ContentType = new("video/mp4");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var response = await provider.VideoRequest(new VideoRequest
        {
            Model = "alibaba/wan-2.5-t2v-preview",
            Prompt = "A desert drone shot.",
            Duration = 5,
            Resolution = "720p",
            AspectRatio = "16:9",
            ProviderOptions = ProviderOptions(new { webhook = "ignored-but-forwarded" })
        });

        Assert.NotNull(capturedCreate);
        using var payloadDocument = JsonDocument.Parse(await capturedCreate!.Content!.ReadAsStringAsync());
        var payload = payloadDocument.RootElement;
        Assert.Equal("alibaba/wan-2.5-t2v-preview", payload.GetProperty("model").GetString());
        Assert.Equal("A desert drone shot.", payload.GetProperty("prompt").GetString());
        Assert.Equal(5, payload.GetProperty("duration").GetInt32());
        Assert.Equal("720p", payload.GetProperty("resolution").GetString());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("ignored-but-forwarded", payload.GetProperty("webhook").GetString());

        Assert.True(pollCount >= 2);
        var video = Assert.Single(response.Videos ?? []);
        Assert.Equal(Convert.ToBase64String(videoBytes), video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.True(response.ProviderMetadata?.ContainsKey("thalam"));
    }

    private static ThalamProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new ThalamProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static Dictionary<string, JsonElement> ProviderOptions(object metadata)
        => new()
        {
            ["thalam"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json);
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
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
