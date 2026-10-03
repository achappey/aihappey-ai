using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.LLMGateway;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.LLMGateway;

public sealed class LLMGatewayProviderImageTests
{
    private const string Base64 = "aW1hZ2U=";

    [Fact]
    public async Task Generation_uses_images_api_maps_options_usage_cost_and_response()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/images/generations", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            Assert.Equal(MediaTypeNames.Application.Json, request.Content!.Headers.ContentType?.MediaType);
            using var doc = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            var payload = doc.RootElement;
            Assert.Equal("gemini-3-pro-image", payload.GetProperty("model").GetString());
            Assert.Equal("A cat", payload.GetProperty("prompt").GetString());
            Assert.Equal(2, payload.GetProperty("n").GetInt32());
            Assert.Equal("4K", payload.GetProperty("size").GetString());
            Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
            Assert.Equal("max", payload.GetProperty("quality").GetString());
            Assert.Equal("low", payload.GetProperty("moderation").GetString());
            Assert.Equal("priority", payload.GetProperty("service_tier").GetString());
            Assert.Equal("natural", payload.GetProperty("style").GetString());
            Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
            Assert.False(payload.TryGetProperty("messages", out _));
            Assert.False(payload.TryGetProperty("image_config", out _));
            Assert.False(payload.TryGetProperty("images", out _));
            Assert.False(payload.TryGetProperty("stream", out _));
            var result = JsonResponse(new
            {
                model = "google/gemini-3-pro-image",
                created = 1677649456,
                data = new[] { new { b64_json = Base64 }, new { b64_json = Base64 } },
                usage = new
                {
                    input_tokens = 10,
                    input_tokens_details = new { image_tokens = 0, text_tokens = 10 },
                    output_tokens = 229,
                    output_tokens_details = new { image_tokens = 229, text_tokens = 0 },
                    total_tokens = 239,
                    cost = 0.00692m,
                    cost_details = new { input_cost = 0.00005m, image_output_cost = 0.00687m }
                }
            });
            result.Headers.Add("x-request-id", "image-request");
            return result;
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "gemini-3-pro-image", Prompt = "A cat", N = 2, Size = "4K", AspectRatio = "16:9", Files = [],
            ProviderOptions = Options(new
            {
                model = "ignored", prompt = "ignored", images = new[] { "ignored" },
                n = 1, size = "1K", aspect_ratio = "1:1", quality = "max", moderation = "low",
                service_tier = "priority", style = "natural", response_format = "url", stream = true
            })
        });

        Assert.Equal(2, response.Images!.Count());
        Assert.All(response.Images!, image => Assert.Equal($"data:image/png;base64,{Base64}", image));
        Assert.Equal(10, response.Usage?.InputTokens);
        Assert.Equal(229, response.Usage?.OutputTokens);
        Assert.Equal(239, response.Usage?.TotalTokens);
        Assert.Equal("llmgateway/google/gemini-3-pro-image", response.Response.ModelId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1677649456).UtcDateTime, response.Response.Timestamp);
        Assert.Equal("image-request", response.Response.Headers!["x-request-id"]);
        var metadata = response.ProviderMetadata!["llmgateway"];
        Assert.False(metadata.TryGetProperty("data", out _));
        var usage = metadata.GetProperty("usage");
        Assert.Equal(0.00692m, usage.GetProperty("cost").GetDecimal());
        Assert.Equal(0.00687m, usage.GetProperty("cost_details").GetProperty("image_output_cost").GetDecimal());
        Assert.Equal(229, usage.GetProperty("output_tokens_details").GetProperty("image_tokens").GetInt32());
        Assert.Contains("providerOptions.llmgateway.stream", WarningFeatures(response));
    }

    [Fact]
    public async Task Edit_preserves_all_url_and_inline_references_and_forwards_edit_options()
    {
        var dataUrl = $"data:image/webp;base64,{Base64}";
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/images/edits", request.RequestUri!.AbsolutePath);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var payload = doc.RootElement;
            var images = payload.GetProperty("images").EnumerateArray().ToArray();
            Assert.Equal(4, images.Length);
            Assert.Equal("https://example.com/source.png", images[0].GetProperty("image_url").GetString());
            Assert.Equal($"data:image/jpeg;base64,{Base64}", images[1].GetProperty("image_url").GetString());
            Assert.Equal(dataUrl, images[2].GetProperty("image_url").GetString());
            Assert.Equal(dataUrl, images[3].GetProperty("image_url").GetString());
            Assert.Equal("transparent", payload.GetProperty("background").GetString());
            Assert.Equal("high", payload.GetProperty("input_fidelity").GetString());
            Assert.Equal("webp", payload.GetProperty("output_format").GetString());
            Assert.Equal(80, payload.GetProperty("output_compression").GetInt32());
            Assert.Equal("2K", payload.GetProperty("size").GetString());
            Assert.Equal("5:4", payload.GetProperty("aspect_ratio").GetString());
            Assert.Equal(3, payload.GetProperty("n").GetInt32());
            Assert.Equal("high", payload.GetProperty("quality").GetString());
            Assert.Equal("low", payload.GetProperty("moderation").GetString());
            Assert.Equal("flex", payload.GetProperty("service_tier").GetString());
            Assert.False(payload.TryGetProperty("mask", out _));
            Assert.False(payload.TryGetProperty("seed", out _));
            Assert.False(payload.TryGetProperty("style", out _));
            Assert.False(payload.TryGetProperty("response_format", out _));
            return JsonResponse(new { data = new[] { new { b64_json = Base64 } } });
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "gemini-3.1-flash-image", Prompt = "Edit this", Seed = 42, Mask = new ImageFile(),
            Files = [new ImageFileUrl { Url = "https://example.com/source.png" },
                new ImageFile { MediaType = "image/jpeg", Data = Base64 },
                new ImageFile { Data = dataUrl }, new ImageFileUrl { Url = dataUrl }],
            ProviderOptions = Options(new { background = "transparent", input_fidelity = "high", output_format = "webp",
                output_compression = 80, quality = "high", moderation = "low", service_tier = "flex",
                n = 3, size = "2K", aspect_ratio = "5:4", style = "vivid", response_format = "url" })
        });
        Assert.Equal($"data:image/webp;base64,{Base64}", Assert.Single(response.Images!));
        Assert.Contains("mask", WarningFeatures(response));
        Assert.Contains("seed", WarningFeatures(response));
        Assert.DoesNotContain("files", WarningFeatures(response));
        Assert.Null(response.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAI_models_omit_aspect_ratio_and_keep_literal_size(bool edit)
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(doc.RootElement.TryGetProperty("aspect_ratio", out _));
            Assert.Equal("3072x2160", doc.RootElement.GetProperty("size").GetString());
            return JsonResponse(new { data = new[] { new { b64_json = Base64 } } });
        });
        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "azure/gpt-image-2", Prompt = "A cat", Size = "3072x2160", AspectRatio = "16:9",
            Files = edit ? [new ImageFile { MediaType = "image/png", Data = Base64 }] : null
        });
        Assert.Contains("aspectRatio", WarningFeatures(response));
    }

    [Theory]
    [InlineData("iVBORw0KGgo=", "image/png")]
    [InlineData("/9j/", "image/jpeg")]
    [InlineData("UklGRgAAAABXRUJQ", "image/webp")]
    [InlineData("data:image/jpeg;base64,aW1hZ2U=", "image/jpeg")]
    public async Task Response_uses_actual_image_mime_type(string value, string mimeType)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(new
        {
            output_format = "png", data = new[] { new { b64_json = value } }
        })));
        var response = await provider.ImageRequest(Request());
        var expected = value.StartsWith("data:") ? value : $"data:{mimeType};base64,{value}";
        Assert.Equal(expected, Assert.Single(response.Images!));
    }

    [Fact]
    public async Task Empty_safety_response_retains_usage_and_cost()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(new
        {
            data = Array.Empty<object>(), usage = new { input_tokens = 10, output_tokens = 0, total_tokens = 10, cost = 0.00005m }
        })));
        var before = DateTime.UtcNow;
        var response = await provider.ImageRequest(Request());
        Assert.Empty(response.Images!);
        Assert.Equal(10, response.Usage?.InputTokens);
        Assert.Equal(0, response.Usage?.OutputTokens);
        Assert.Equal(0.00005m, response.ProviderMetadata!["llmgateway"].GetProperty("usage").GetProperty("cost").GetDecimal());
        Assert.Contains("images", WarningFeatures(response));
        Assert.Equal("llmgateway/gemini-3-pro-image", response.Response.ModelId);
        Assert.InRange(response.Response.Timestamp, before, DateTime.UtcNow);
    }

    [Theory]
    [InlineData("file_id")]
    [InlineData("fileId")]
    public async Task File_ids_are_rejected_before_http(string type)
    {
        var provider = NoHttpProvider();
        var request = Request();
        request.Files = [new ImageFile { Type = type, Data = "file-123" }];
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(request));
        Assert.Contains("file IDs", error.Message);
    }

    [Fact]
    public async Task Malformed_files_are_rejected_before_http()
    {
        ImageFile[] files = [new ImageFileUrl { Url = "http://example.com/image.png" },
            new ImageFileUrl { Url = "not-a-url" }, new ImageFile { Data = Base64 },
            new ImageFile { MediaType = "text/plain", Data = Base64 },
            new ImageFile { MediaType = "image/png", Data = "invalid!" },
            new ImageFile { Data = "data:text/plain;base64,aW1hZ2U=" },
            new ImageFile { Data = "data:image/png;base64," },
            new ImageFile { Data = "data:image/png,hello" }];
        foreach (var file in files)
        {
            var request = Request();
            request.Files = [file];
            await Assert.ThrowsAsync<ArgumentException>(() => NoHttpProvider().ImageRequest(request));
        }
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_before_http()
    {
        var provider = NoHttpProvider();
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.ImageRequest(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(new ImageRequest { Model = "image" }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(new ImageRequest { Prompt = "cat" }));
        foreach (var n in new[] { 0, 11 })
        {
            var request = Request();
            request.N = n;
            await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(request));
        }
        var invalidOptions = Request();
        invalidOptions.ProviderOptions = Options("not an object");
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(invalidOptions));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[{}]}")]
    public async Task Malformed_responses_are_not_treated_as_safety_filtering(string json)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(Request()));
    }

    [Fact]
    public async Task Http_errors_preserve_status_and_body()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("provider error")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(Request()));
        Assert.Contains("400", error.Message);
        Assert.Contains("provider error", error.Message);
    }

    [Fact]
    public async Task Cancellation_reaches_http_transport()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = CreateProvider(async (_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse(new { });
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ImageRequest(Request(), cancellation.Token));
    }

    private static ImageRequest Request() => new() { Model = "gemini-3-pro-image", Prompt = "A cat" };
    private static IEnumerable<string?> WarningFeatures(ImageResponse response)
        => JsonSerializer.SerializeToElement(response.Warnings).EnumerateArray().Select(w => w.GetProperty("feature").GetString());
    private static Dictionary<string, JsonElement> Options(object value)
        => new() { ["llmgateway"] = JsonSerializer.SerializeToElement(value) };
    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
    };
    private static LLMGatewayProvider NoHttpProvider()
        => CreateProvider((_, _) => throw new InvalidOperationException("HTTP must not be called."));
    private static LLMGatewayProvider CreateProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));
    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
