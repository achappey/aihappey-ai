using System.Net;
using System.Reflection;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenRouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenRouter;

public sealed class OpenRouterProviderImageTests
{
    [Fact]
    public void BuildImagePayloadUsesNativeOpenRouterImageShape()
    {
        var payload = BuildPayload(new ImageRequest
        {
            Model = "bytedance-seed/seedream-4.5",
            Prompt = "a red panda astronaut floating in space, studio lighting",
            Size = "2K",
            AspectRatio = "16:9",
            N = 2,
            Seed = 123,
            Files =
            [
                Image("image/png", "reference-one"),
                Image("image/jpeg", "https://example.com/reference-two.jpg")
            ]
        });

        Assert.Equal("bytedance-seed/seedream-4.5", payload.GetProperty("model").GetString());
        Assert.Equal("a red panda astronaut floating in space, studio lighting", payload.GetProperty("prompt").GetString());
        Assert.Equal("2K", payload.GetProperty("size").GetString());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal(2, payload.GetProperty("n").GetInt32());
        Assert.Equal(123, payload.GetProperty("seed").GetInt32());
        Assert.False(payload.TryGetProperty("messages", out _));
        Assert.False(payload.TryGetProperty("modalities", out _));
        Assert.False(payload.TryGetProperty("image_config", out _));

        var references = payload.GetProperty("input_references").EnumerateArray().ToList();
        Assert.Equal(2, references.Count);
        Assert.Equal("image_url", references[0].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,reference-one", references[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("https://example.com/reference-two.jpg", references[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildImagePayloadLetsOpenRouterProviderOptionsOverrideStandardFields()
    {
        using var providerOptionsDoc = JsonDocument.Parse("""
        {
          "openrouter": {
            "size": "2048x2048",
            "aspect_ratio": "1:1",
            "n": 1,
            "input_references": [
              {
                "type": "image_url",
                "image_url": {
                  "url": "https://example.com/override.png"
                }
              }
            ]
          }
        }
        """);

        var payload = BuildPayload(new ImageRequest
        {
            Model = "bytedance-seed/seedream-4.5",
            Prompt = "provider override",
            Size = "2K",
            AspectRatio = "16:9",
            N = 3,
            Files = [Image("image/png", "standard-reference")],
            ProviderOptions = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                providerOptionsDoc.RootElement.GetRawText(), JsonSerializerOptions.Web)
        });

        Assert.Equal("2048x2048", payload.GetProperty("size").GetString());
        Assert.Equal("1:1", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal(1, payload.GetProperty("n").GetInt32());
        var reference = payload.GetProperty("input_references").EnumerateArray().Single();
        Assert.Equal("https://example.com/override.png", reference.GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task ImageRequestPostsToNativeImagesEndpointAndNormalizesResponse()
    {
        string? requestedPath = null;
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "created": 1748372400,
                  "data": [
                    { "b64_json": "png-base64" },
                    { "b64_json": "svg-base64", "media_type": "image/svg+xml" }
                  ],
                  "usage": {
                    "prompt_tokens": 10,
                    "completion_tokens": 20,
                    "total_tokens": 30
                  }
                }
                """)
            };
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "bytedance-seed/seedream-4.5",
            Prompt = "native endpoint",
            N = 2,
            Seed = 42
        });

        Assert.Equal("/api/v1/images", requestedPath);
        Assert.Contains("\"prompt\":\"native endpoint\"", requestJson);
        Assert.Contains("\"n\":2", requestJson);
        Assert.Contains("\"seed\":42", requestJson);
        Assert.DoesNotContain("chat", requestedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["data:image/png;base64,png-base64", "data:image/svg+xml;base64,svg-base64"], result.Images);
        Assert.Empty(result.Warnings);
        Assert.Equal(10, result.Usage?.InputTokens);
        Assert.Equal(20, result.Usage?.OutputTokens);
        Assert.Equal(30, result.Usage?.TotalTokens);
        Assert.NotNull(result.ProviderMetadata);
        Assert.True(result.ProviderMetadata!.ContainsKey("openrouter"));
    }

    [Fact]
    public async Task ImageRequestWarnsForMaskOnly()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "created": 1748372400,
              "data": [
                { "b64_json": "image-base64" }
              ]
            }
            """)
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "bytedance-seed/seedream-4.5",
            Prompt = "masked image",
            N = 1,
            Seed = 1,
            Mask = Image("image/png", "mask-base64")
        });

        var warningJson = JsonSerializer.Serialize(result.Warnings);
        Assert.Contains("mask", warningJson);
        Assert.DoesNotContain("seed", warningJson);
        Assert.DoesNotContain("\"n\"", warningJson);
    }

    private static JsonElement BuildPayload(ImageRequest request)
    {
        var readOptions = typeof(OpenRouterProvider).GetMethod("ReadOpenRouterImageProviderOptions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(OpenRouterProvider), "ReadOpenRouterImageProviderOptions");
        var buildPayload = typeof(OpenRouterProvider).GetMethod("BuildOpenRouterImagePayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(OpenRouterProvider), "BuildOpenRouterImagePayload");

        try
        {
            var rawOptions = readOptions.Invoke(null, [request]);
            var payload = buildPayload.Invoke(null, [request, rawOptions])!;
            return JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static OpenRouterProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static ImageFile Image(string mediaType, string data)
        => new()
        {
            MediaType = mediaType,
            Data = data
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
