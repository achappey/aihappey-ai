using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.VanceAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.VanceAI;

public sealed class VanceAIProviderTests
{
    [Fact]
    public async Task ListModels_exposes_all_documented_tools_with_media_types()
    {
        var provider = CreateProvider(new HttpClient(), new HttpClient());

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal(11, models.Count);
        foreach (var slug in new[] { "upscale", "sharpen", "denoise", "remove_bg", "restore", "cartoonize", "passport_photo", "custom" })
            Assert.Equal("image", models[$"vanceai/{slug}"].Type);
        foreach (var slug in new[] { "video_upscale", "video_hdr", "video_face_enhance" })
            Assert.Equal("video", models[$"vanceai/{slug}"].Type);
    }

    [Fact]
    public async Task ImageRequest_uses_public_url_passes_raw_config_and_downloads_result()
    {
        JsonElement? submitted = null;
        var calls = 0;
        byte[] expected = [1, 2, 3, 4];
        var api = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Post)
            {
                submitted = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
                return Json(HttpStatusCode.Accepted, new { job_id = "job-image", status = "queued", tool = "upscale", media = "image" });
            }
            if (request.RequestUri?.AbsolutePath.EndsWith("/result", StringComparison.Ordinal) == true)
                return Bytes(expected, "image/webp");
            calls++;
            return Json(HttpStatusCode.OK, new { job_id = "job-image", status = calls == 1 ? "processing" : "succeeded", progress = calls == 1 ? 50 : 100, tool = "upscale", media = "image" });
        })) { BaseAddress = new Uri("https://vanceai.com/api/") };
        var provider = CreateProvider(api, new HttpClient());
        var options = JsonDocument.Parse("""{"scale":4,"face_enhance":true,"output_format":"webp"}""").RootElement.Clone();

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "upscale",
            Prompt = "ignored",
            Files = [new ImageFile { Type = "url", MediaType = "image/jpeg", Data = "https://example.test/input.jpg" }],
            ProviderOptions = new() { ["vanceai"] = options }
        });

        Assert.Equal("https://example.test/input.jpg", submitted!.Value.GetProperty("image_url").GetString());
        Assert.Equal("upscale", submitted.Value.GetProperty("tool").GetString());
        Assert.Equal(4, submitted.Value.GetProperty("config").GetProperty("scale").GetInt32());
        Assert.True(submitted.Value.GetProperty("config").GetProperty("face_enhance").GetBoolean());
        Assert.Equal("webp", submitted.Value.GetProperty("output_format").GetString());
        Assert.Equal($"data:image/webp;base64,{Convert.ToBase64String(expected)}", Assert.Single(result.Images!));
    }

    [Fact]
    public async Task StartVideoOperation_uses_first_video_reference_and_source_url()
    {
        JsonElement? submitted = null;
        var api = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            submitted = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return Json(HttpStatusCode.Accepted, new { job_id = "job-video", status = "queued", tool = "video_hdr", media = "video" });
        })) { BaseAddress = new Uri("https://vanceai.com/api/") };
        var provider = CreateProvider(api, new HttpClient());
        var options = JsonDocument.Parse("""{"quality":"high","codec":"h265","hdr_color_gamut":"rec2020"}""").RootElement.Clone();

        var result = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "video_hdr",
            Prompt = "",
            InputReferences =
            [
                new VideoFile { MediaType = "image/png", Data = "AQID" },
                new VideoFile { MediaType = "video/mp4", Data = "https://example.test/input.mp4" },
                new VideoFile { MediaType = "video/mp4", Data = "https://example.test/ignored.mp4" }
            ],
            ProviderOptions = new() { ["vanceai"] = options }
        });

        Assert.Equal("https://example.test/input.mp4", submitted!.Value.GetProperty("source_url").GetString());
        Assert.Equal("video_hdr", submitted.Value.GetProperty("tool").GetString());
        Assert.Equal("high", submitted.Value.GetProperty("config").GetProperty("quality").GetString());
        Assert.Equal("h265", submitted.Value.GetProperty("config").GetProperty("codec").GetString());
        Assert.StartsWith("job-video.", result.Operation);
        Assert.Equal("vanceai/video_hdr", result.Response.ModelId);
    }

    [Fact]
    public async Task StartVideoOperation_uploads_inline_video_without_bearer_on_presigned_put()
    {
        AuthenticationHeaderValue? putAuthorization = new("sentinel");
        byte[]? uploaded = null;
        JsonElement? submitted = null;
        var call = 0;
        var api = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            call++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (call == 1)
                return Json(HttpStatusCode.Created, new { upload_id = "upl-1", put_url = "https://storage.test/upload", max_bytes = 5_000_000_000L });
            submitted = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return Json(HttpStatusCode.Accepted, new { job_id = "job-uploaded", status = "queued", tool = "video_upscale", media = "video" });
        })) { BaseAddress = new Uri("https://vanceai.com/api/") };
        var transfer = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            putAuthorization = request.Headers.Authorization;
            uploaded = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var provider = CreateProvider(api, transfer);

        await provider.StartVideoOperation(new VideoRequest
        {
            Model = "video_upscale",
            Prompt = "",
            InputReferences = [new VideoFile { MediaType = "video/mp4", Data = Convert.ToBase64String([9, 8, 7]) }]
        });

        Assert.Null(putAuthorization);
        Assert.Equal(new byte[] { 9, 8, 7 }, uploaded);
        Assert.Equal("upl-1", submitted!.Value.GetProperty("upload_id").GetString());
    }

    [Fact]
    public async Task StartVideoOperation_rejects_missing_video_input()
    {
        var provider = CreateProvider(new HttpClient(), new HttpClient());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => provider.StartVideoOperation(new VideoRequest
        {
            Model = "video_upscale",
            Prompt = "",
            InputReferences = [new VideoFile { MediaType = "image/png", Data = "AQID" }]
        }));
        Assert.Contains("video/*", error.Message);
    }

    [Fact]
    public async Task GetVideoOperationStatus_maps_pending_failed_and_completed_result()
    {
        var statuses = new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, new { job_id = "pending", status = "processing", phase = "uploading", progress = 0, tool = "video_upscale", media = "video" }),
            Json(HttpStatusCode.OK, new { job_id = "failed", status = "failed", tool = "video_upscale", media = "video", error = new { code = "processing_failed", message = "engine failed" } }),
            Json(HttpStatusCode.OK, new { job_id = "done", status = "succeeded", progress = 100, tool = "video_upscale", media = "video" }),
            Bytes([4, 5, 6], "video/mp4")
        ]);
        var api = new HttpClient(new DelegateHttpMessageHandler(_ => statuses.Dequeue())) { BaseAddress = new Uri("https://vanceai.com/api/") };
        var provider = CreateProvider(api, new HttpClient());

        Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus(Token("pending", "video_upscale")));
        var failed = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus(Token("failed", "video_upscale")));
        Assert.Contains("processing_failed", failed.Error);
        var completed = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus(Token("done", "video_upscale")));
        var video = Assert.Single(completed.Videos);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(Convert.ToBase64String([4, 5, 6]), video.Data);
    }

    private static VanceAIProvider CreateProvider(HttpClient api, HttpClient transfer)
        => new(new StaticApiKeyResolver(), new SequencedHttpClientFactory(api, transfer));

    private static string Token(string jobId, string model)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(model)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{jobId}.{payload}";
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes, string mediaType)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(mediaType) } } };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "sk_live_test";
    }

    private sealed class SequencedHttpClientFactory(params HttpClient[] clients) : IHttpClientFactory
    {
        private int _index;
        public HttpClient CreateClient(string name) => clients[_index++];
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
