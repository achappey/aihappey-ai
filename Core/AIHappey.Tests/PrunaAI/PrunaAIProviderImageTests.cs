using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.PrunaAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.PrunaAI;

public sealed class PrunaAIProviderImageTests
{
    [Fact]
    public async Task ImageRequest_sends_exact_allowed_content_type_and_documented_payload()
    {
        const string generationUrl = "https://api.pruna.ai/v1/predictions/delivery/test/output.jpg";
        var imageBytes = Encoding.UTF8.GetBytes("generated-image");
        HttpMethod? predictionMethod = null;
        string? predictionPath = null;
        string? apiKey = null;
        string? model = null;
        string? trySync = null;
        string? contentType = null;
        string? predictionBody = null;

        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/predictions")
            {
                predictionMethod = request.Method;
                predictionPath = request.RequestUri.AbsolutePath;
                apiKey = request.Headers.TryGetValues("apikey", out var apiKeys) ? Assert.Single(apiKeys) : null;
                model = request.Headers.TryGetValues("Model", out var models) ? Assert.Single(models) : null;
                trySync = request.Headers.TryGetValues("Try-Sync", out var syncValues) ? Assert.Single(syncValues) : null;
                contentType = request.Content?.Headers.ContentType?.ToString();
                predictionBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

                return JsonResponse(new { status = "succeeded", generation_url = generationUrl });
            }

            Assert.Equal(generationUrl, request.RequestUri?.ToString());
            Assert.Equal("test-api-key", request.Headers.TryGetValues("apikey", out var downloadApiKeys)
                ? Assert.Single(downloadApiKeys)
                : null);
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Image.Jpeg);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = imageContent };
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "p-image",
            Prompt = "A majestic lion standing on a rocky cliff at sunset",
            AspectRatio = "16:9"
        });

        Assert.Equal(HttpMethod.Post, predictionMethod);
        Assert.Equal("/v1/predictions", predictionPath);
        Assert.Equal("test-api-key", apiKey);
        Assert.Equal("p-image", model);
        Assert.Equal("true", trySync);
        Assert.Equal(MediaTypeNames.Application.Json, contentType);

        using var payloadDocument = JsonDocument.Parse(Assert.IsType<string>(predictionBody));
        var input = payloadDocument.RootElement.GetProperty("input");
        Assert.Equal("A majestic lion standing on a rocky cliff at sunset", input.GetProperty("prompt").GetString());
        Assert.Equal("16:9", input.GetProperty("aspect_ratio").GetString());
        Assert.Equal(2, input.EnumerateObject().Count());

        var image = Assert.Single(response.Images ?? []);
        Assert.Equal($"data:{MediaTypeNames.Image.Jpeg};base64,{Convert.ToBase64String(imageBytes)}", image);
        Assert.Equal("prunaai/p-image", response.Response.ModelId);
    }

    private static PrunaAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var factory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new PrunaAIProvider(new StaticApiKeyResolver(), cache, factory);
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
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
