using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIProviderImageTests
{
    [Fact]
    public async Task ImageRequestWithoutFilesPostsGenerationJsonAndEnrichesUsageMetadataAndCost()
    {
        string? requestedPath = null;
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse("""
            {
              "created": 1748372400,
              "output_format": "png",
              "data": [
                { "b64_json": "image-base64" }
              ],
              "usage": {
                "input_tokens": 50,
                "input_tokens_details": {
                  "text_tokens": 10,
                  "image_tokens": 40
                },
                "output_tokens": 50,
                "output_tokens_details": {
                  "image_tokens": 45,
                  "text_tokens": 5
                },
                "total_tokens": 100
              }
            }
            """);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "gpt-image-1.5",
            Prompt = "native generation",
            N = 1,
            Size = "1024x1024"
        });

        Assert.Equal("/v1/images/generations", requestedPath);
        Assert.Contains("\"prompt\":\"native generation\"", requestJson);
        Assert.Contains("\"output_format\":\"png\"", requestJson);
        Assert.Equal(["data:image/png;base64,image-base64"], result.Images);
        Assert.Equal(50, result.Usage?.InputTokens);
        Assert.Equal(50, result.Usage?.OutputTokens);
        Assert.Equal(100, result.Usage?.TotalTokens);

        var openAiMetadata = Assert.Contains("openai", result.ProviderMetadata ?? []);
        Assert.Equal(100, openAiMetadata.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        var gatewayMetadata = Assert.Contains("gateway", result.ProviderMetadata ?? []);
        Assert.Equal(0.00191m, gatewayMetadata.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task ImageRequestWithSingleFilePostsVariationMultipart()
    {
        string? requestedPath = null;
        string? contentType = null;
        string? body = null;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse("""
            {
              "created": 1589478378,
              "data": [
                { "b64_json": "variation-base64" }
              ]
            }
            """);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "dall-e-2",
            Prompt = "ignored prompt",
            N = 2,
            Size = "1024x1024",
            Files = [Image("image/png", Convert.ToBase64String([1, 2, 3]))]
        });

        Assert.Equal("/v1/images/variations", requestedPath);
        Assert.Equal("multipart/form-data", contentType);
        Assert.Contains("name=model", body);
        Assert.Contains("dall-e-2", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("b64_json", body);
        Assert.Equal(["data:image/png;base64,variation-base64"], result.Images);
        Assert.Contains("prompt", JsonSerializer.Serialize(result.Warnings));
    }

    [Fact]
    public async Task ImageRequestWithMultipleFilesPostsEditJsonWithImageReferences()
    {
        string? requestedPath = null;
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse("""
            {
              "created": 1748372400,
              "data": [
                { "b64_json": "edit-base64" }
              ],
              "usage": {
                "input_tokens": 30,
                "output_tokens": 20,
                "total_tokens": 50
              }
            }
            """);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "gpt-image-1-mini",
            Prompt = "combine these images",
            Files =
            [
                Image("image/png", "first-base64"),
                Image("image/jpeg", "https://example.com/second.jpg")
            ]
        });

        Assert.Equal("/v1/images/edits", requestedPath);
        Assert.Contains("\"prompt\":\"combine these images\"", requestJson);
        Assert.Contains("\"images\"", requestJson);
        Assert.Contains("data:image/png;base64,first-base64", requestJson);
        Assert.Contains("https://example.com/second.jpg", requestJson);
        Assert.Contains("\"output_format\":\"png\"", requestJson);
        Assert.Equal(["data:image/png;base64,edit-base64"], result.Images);
    }

    [Fact]
    public async Task OpenAIImageGenerationRequestAsync_UsesReusableOpenAICompatibleHelper()
    {
        string? requestedPath = null;
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse("""
            {
              "created": 1748372400,
              "data": [ { "b64_json": "native-base64" } ],
              "usage": { "input_tokens": 1, "output_tokens": 2, "total_tokens": 3 }
            }
            """);
        });

        var result = await provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = "gpt-image-1.5",
            Prompt = "native generation",
            N = 1,
            Size = "1024x1024"
        });

        Assert.Equal("/v1/images/generations", requestedPath);
        Assert.Contains("\"prompt\":\"native generation\"", requestJson);
        Assert.Equal("native-base64", result.Data!.Single().B64Json);
        Assert.Equal(3, result.Usage!.TotalTokens);
    }

    [Fact]
    public async Task OpenAIImageGenerationStreamingAsync_ParsesSseEvents()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/images/generations", request.RequestUri?.PathAndQuery);
            var requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"stream\":true", requestJson);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                event: image_generation.partial_image
                data: {"type":"image_generation.partial_image","b64_json":"partial","created_at":1,"partial_image_index":0}

                event: image_generation.completed
                data: {"type":"image_generation.completed","b64_json":"final","created_at":2,"usage":{"total_tokens":3}}

                """)
            };
        });

        var events = new List<IOpenAIImageStreamEvent>();
        await foreach (var streamEvent in provider.OpenAIImageGenerationStreamingAsync(new OpenAIImageGenerationRequest
        {
            Model = "gpt-image-1.5",
            Prompt = "stream this"
        }))
        {
            events.Add(streamEvent);
        }

        var partial = Assert.IsType<OpenAIImageGenerationPartialImage>(events[0]);
        var completed = Assert.IsType<OpenAIImageGenerationCompleted>(events[1]);
        Assert.Equal("partial", partial.B64Json);
        Assert.Equal("final", completed.B64Json);
        Assert.Equal(3, completed.Usage!.TotalTokens);
    }

    [Fact]
    public async Task OpenAIImageEditRequestAsync_PostsMultipartWhenFilesArePresent()
    {
        string? requestedPath = null;
        string? contentType = null;
        string? body = null;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse("""
            {
              "created": 1748372400,
              "data": [ { "b64_json": "edit-native" } ]
            }
            """);
        });

        var result = await provider.OpenAIImageEditRequestAsync(new OpenAIImageEditRequest
        {
            Model = "gpt-image-1.5",
            Prompt = "edit native",
            ImageFiles = [FormImage("image", "image.png", "image/png")],
            Stream = false
        });

        Assert.Equal("/v1/images/edits", requestedPath);
        Assert.Equal("multipart/form-data", contentType);
        Assert.Contains("name=model", body);
        Assert.Contains("gpt-image-1.5", body);
        Assert.Contains("name=image[]", body);
        Assert.Equal("edit-native", result.Data!.Single().B64Json);
    }

    [Fact]
    public async Task OpenAIImageVariationRequestAsync_PostsMultipartImage()
    {
        string? requestedPath = null;
        string? contentType = null;
        string? body = null;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return JsonResponse("""
            {
              "created": 1748372400,
              "data": [ { "url": "https://example.com/image.png" } ]
            }
            """);
        });

        var result = await provider.OpenAIImageVariationRequestAsync(new OpenAIImageVariationRequest
        {
            Model = "dall-e-2",
            ImageFile = FormImage("image", "image.png", "image/png"),
            N = 1,
            ResponseFormat = "url"
        });

        Assert.Equal("/v1/images/variations", requestedPath);
        Assert.Equal("multipart/form-data", contentType);
        Assert.Contains("name=image", body);
        Assert.Equal("https://example.com/image.png", result.Data!.Single().Url);
    }

    private static OpenAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new NullEndUserIdResolver());

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

    private static ImageFile Image(string mediaType, string data)
        => new()
        {
            MediaType = mediaType,
            Data = data
        };

    private static IFormFile FormImage(string name, string fileName, string contentType)
    {
        var bytes = new byte[] { 1, 2, 3 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, name, fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class NullEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
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
