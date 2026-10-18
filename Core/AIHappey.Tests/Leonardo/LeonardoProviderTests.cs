using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Leonardo;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Leonardo;

public sealed class LeonardoProviderTests
{
    [Fact]
    public async Task ListModels_preserves_discovered_model_identifiers_and_metadata()
    {
        string? path = null;
        string? authorizationScheme = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            authorizationScheme = request.Headers.Authorization?.Scheme;
            return JsonResponse("""
                {
                  "custom_models": [
                    {
                      "id": "phoenix-model",
                      "name": "Phoenix",
                      "description": "A platform image model",
                      "nsfw": false
                    }
                  ]
                }
                """);
        });

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("/api/rest/v1/platformModels", path);
        Assert.Equal("Bearer", authorizationScheme);
        Assert.Equal("leonardo/phoenix-model", model.Id);
        Assert.Equal("Phoenix", model.Name);
        Assert.Equal("image", model.Type);
        Assert.Equal("A platform image model", model.Description);
        Assert.False(model.Metadata!["nsfw"].GetBoolean());
    }

    [Fact]
    public async Task ImageRequest_forwards_native_options_and_polls_for_generated_images()
    {
        var apiRequests = new List<string>();
        string? generationBody = null;
        string? imageAuthorization = "unset";
        var polls = 0;
        var imageBytes = Encoding.UTF8.GetBytes("image");
        var provider = CreateProvider(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "cloud.leonardo.ai")
            {
                apiRequests.Add($"{request.Method} {uri.AbsolutePath}");
                Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), request.Headers.Authorization);

                if (request.Method == HttpMethod.Post)
                {
                    generationBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return JsonResponse("""{"sdGenerationJob":{"generationId":"generation-123"}}""");
                }

                polls++;
                return polls == 1
                    ? JsonResponse("""{"generations_by_pk":{"status":"PENDING","generated_images":[]}}""")
                    : JsonResponse("""
                        {
                          "generations_by_pk": {
                            "status": "COMPLETE",
                            "generated_images": [{"id": "image-123", "url": "https://cdn.example.com/image.png"}]
                          }
                        }
                        """);
            }

            imageAuthorization = request.Headers.Authorization?.ToString();
            var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Image.Png);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "leonardo/phoenix-model",
            Prompt = "A mountain at sunrise",
            Size = "1024x768",
            N = 2,
            Seed = 17,
            ProviderOptions = new()
            {
                ["leonardo"] = JsonSerializer.SerializeToElement(new { alchemy = true, contrast = 3.5 })
            }
        });

        using var body = JsonDocument.Parse(generationBody!);
        var payload = body.RootElement;
        Assert.Equal("phoenix-model", payload.GetProperty("modelId").GetString());
        Assert.Equal("A mountain at sunrise", payload.GetProperty("prompt").GetString());
        Assert.Equal(1024, payload.GetProperty("width").GetInt32());
        Assert.Equal(768, payload.GetProperty("height").GetInt32());
        Assert.Equal(2, payload.GetProperty("num_images").GetInt32());
        Assert.Equal(17, payload.GetProperty("seed").GetInt32());
        Assert.True(payload.GetProperty("alchemy").GetBoolean());
        Assert.Equal(3.5, payload.GetProperty("contrast").GetDouble());
        Assert.Equal(
            [
                "POST /api/rest/v1/generations",
                "GET /api/rest/v1/generations/generation-123",
                "GET /api/rest/v1/generations/generation-123"
            ],
            apiRequests);
        Assert.Null(imageAuthorization);
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}",
            Assert.Single(response.Images!));
        Assert.Equal("leonardo/phoenix-model", response.Response.ModelId);
        Assert.Equal(
            "generation-123",
            response.ProviderMetadata!["leonardo"]
                .GetProperty("generation")
                .GetProperty("sdGenerationJob")
                .GetProperty("generationId")
                .GetString());
    }

    [Fact]
    public async Task ImageRequest_throws_when_generation_fails()
    {
        var provider = CreateProvider(request => request.Method == HttpMethod.Post
            ? JsonResponse("""{"sdGenerationJob":{"generationId":"generation-123"}}""")
            : JsonResponse("""{"generations_by_pk":{"status":"FAILED","generated_images":[]}}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImageRequest(new ImageRequest
            {
                Model = "leonardo/model",
                Prompt = "A mountain"
            }));

        Assert.Contains("generation-123", error.Message);
        Assert.Contains("failed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static LeonardoProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StaticResponseHandler(responder));
    }

    private sealed class StaticResponseHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
