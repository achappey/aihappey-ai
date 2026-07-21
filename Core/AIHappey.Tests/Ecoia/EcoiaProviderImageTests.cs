using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Ecoia;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Ecoia;

public sealed class EcoiaProviderImageTests
{
    [Fact]
    public async Task ImageRequest_posts_ecosia_payload_and_maps_images_usage_metadata_and_headers()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/image", request.RequestUri?.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("gemini-3.1-pro-image", payload.RootElement.GetProperty("model").GetString());
            var content = payload.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("A serene forest at dawn", content[0].GetProperty("text").GetString());
            Assert.Equal("data:image/jpeg;base64,aW5wdXQ=", content[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("1024x1024", payload.RootElement.GetProperty("settings").GetProperty("size").GetString());
            Assert.Equal("16:9", payload.RootElement.GetProperty("settings").GetProperty("aspectRatio").GetString());
            Assert.Equal("IMAGE", payload.RootElement.GetProperty("responseModalities")[0].GetString());

            var response = JsonResponse(HttpStatusCode.OK, """
            {
              "success": true,
              "images": ["data:image/png;base64,b3V0cHV0"],
              "usage": { "input_tokens": 7, "output_tokens": 2 },
              "ethical_ai_metadata": { "co2_grams": 0.002, "water_liters": 0.02 }
            }
            """);
            response.Headers.Add("x-ecoia-carbon", "0.002");
            return response;
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "gemini-3.1-pro-image",
            Prompt = "A serene forest at dawn",
            Size = "1024x1024",
            AspectRatio = "16:9",
            Files = [new ImageFile { MediaType = MediaTypeNames.Image.Jpeg, Data = "aW5wdXQ=" }],
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["ecoia"] = JsonSerializer.SerializeToElement(new
                {
                    responseModalities = new[] { "IMAGE" },
                    settings = new { imageSize = "2K" }
                })
            }
        });

        Assert.Equal(["data:image/png;base64,b3V0cHV0"], result.Images?.ToArray() ?? []);
        Assert.Equal(7, result.Usage?.InputTokens);
        Assert.Equal(2, result.Usage?.OutputTokens);
        Assert.Equal(9, result.Usage?.TotalTokens);
        Assert.Equal("ecoia/gemini-3.1-pro-image", result.Response.ModelId);
        Assert.Equal("0.002", result.Response.Headers?["x-ecoia-carbon"]);
        Assert.Equal(0.002, result.ProviderMetadata!["ecoia"].GetProperty("ethical_ai_metadata").GetProperty("co2_grams").GetDouble());
    }

    [Fact]
    public async Task OpenAI_image_adapters_use_the_native_endpoint_and_synthesize_completed_events()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("custom-image-model", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal("A blue nebula", payload.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
            return JsonResponse(HttpStatusCode.OK, "{" + "\"success\":true,\"images\":[\"data:image/png;base64,b3V0cHV0\"]}");
        });

        var response = await provider.OpenAIImageGenerationRequestAsync(new AIHappey.Core.Models.OpenAIImageGenerationRequest
        {
            Model = "custom-image-model",
            Prompt = "A blue nebula"
        });
        var events = new List<AIHappey.Core.Models.IOpenAIImageStreamEvent>();
        await foreach (var streamEvent in provider.OpenAIImageGenerationStreamingAsync(new AIHappey.Core.Models.OpenAIImageGenerationRequest
        {
            Model = "custom-image-model",
            Prompt = "A blue nebula"
        }))
        {
            events.Add(streamEvent);
        }

        Assert.Equal("b3V0cHV0", response.Data?.Single().B64Json);
        var completed = Assert.IsType<AIHappey.Core.Models.OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.Equal("b3V0cHV0", completed.B64Json);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ImageRequest_throws_for_upstream_or_unsuccessful_ecoia_response()
    {
        var upstreamProvider = CreateProvider(_ => JsonResponse(HttpStatusCode.BadRequest, "{\"error\":\"invalid model\"}"));
        var upstreamException = await Assert.ThrowsAsync<InvalidOperationException>(() => upstreamProvider.ImageRequest(new ImageRequest
        {
            Model = "invalid",
            Prompt = "A test image"
        }));
        Assert.Contains("400", upstreamException.Message, StringComparison.Ordinal);

        var ecoiaProvider = CreateProvider(_ => JsonResponse(HttpStatusCode.OK, "{\"success\":false,\"message\":\"blocked\"}"));
        var ecoiaException = await Assert.ThrowsAsync<InvalidOperationException>(() => ecoiaProvider.ImageRequest(new ImageRequest
        {
            Model = "test-model",
            Prompt = "A test image"
        }));
        Assert.Equal("blocked", ecoiaException.Message);
    }

    [Fact]
    public async Task ImageRequest_throws_when_response_contains_no_usable_images()
    {
        var provider = CreateProvider(_ => JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"images\":[]}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "test-model",
            Prompt = "A test image"
        }));

        Assert.Contains("usable images", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EcoiaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
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
