using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.NebulaBlock;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.NebulaBlock;

public sealed class NebulaBlockProviderImageTests
{
    [Fact]
    public async Task ImageRequest_posts_merged_payload_and_returns_base64_images()
    {
        HttpMethod? requestMethod = null;
        string? requestPath = null;
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestMethod = request.Method;
            requestPath = request.RequestUri?.PathAndQuery;
            authorizationScheme = request.Headers.Authorization?.Scheme;
            authorizationParameter = request.Headers.Authorization?.Parameter;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.OK, """
            {
              "model":"black-forest-labs/FLUX.1-Fill-dev",
              "object":"list",
              "data":[{"index":0,"b64_json":"aW1hZ2U="}],
              "message":"Image generated successfully",
              "status":"success"
            }
            """);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "black-forest-labs/FLUX.1-Fill-dev",
            Prompt = "A red baseball cap",
            Size = "1024x768",
            Files =
            [
                new ImageFile { MediaType = MediaTypeNames.Image.Jpeg, Data = "data:image/jpeg;base64,aW5wdXQ=" },
                new ImageFile { MediaType = MediaTypeNames.Image.Jpeg, Data = "aWdub3JlZA==" }
            ],
            Mask = new ImageFile { MediaType = MediaTypeNames.Image.Jpeg, Data = "data:image/jpeg;base64,bWFzaw==" },
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["nebulablock"] = JsonSerializer.SerializeToElement(new
                {
                    num_steps = 40,
                    guidance_scale = 4.5,
                    width = 512,
                    extra_option = "passed-through"
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.Equal(HttpMethod.Post, requestMethod);
        Assert.Equal("/api/v1/images/generation", requestPath);
        Assert.Equal("Bearer", authorizationScheme);
        Assert.Equal("test-key", authorizationParameter);

        using var payload = JsonDocument.Parse(requestJson!);
        Assert.Equal("black-forest-labs/FLUX.1-Fill-dev", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("A red baseball cap", payload.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(512, payload.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(768, payload.RootElement.GetProperty("height").GetInt32());
        Assert.Equal(40, payload.RootElement.GetProperty("num_steps").GetInt32());
        Assert.Equal(4.5, payload.RootElement.GetProperty("guidance_scale").GetDouble());
        Assert.Equal("passed-through", payload.RootElement.GetProperty("extra_option").GetString());
        Assert.Equal("aW5wdXQ=", payload.RootElement.GetProperty("image").GetString());
        Assert.Equal("bWFzaw==", payload.RootElement.GetProperty("mask").GetString());

        Assert.Equal(["data:image/png;base64,aW1hZ2U="], result.Images?.ToArray() ?? []);
        Assert.Equal("nebulablock/black-forest-labs/FLUX.1-Fill-dev", result.Response.ModelId);
        Assert.True(result.ProviderMetadata?.ContainsKey("nebulablock"));
        Assert.Equal("success", result.ProviderMetadata!["nebulablock"].GetProperty("status").GetString());
        Assert.Contains(result.Warnings, warning => JsonSerializer.Serialize(warning).Contains("files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImageRequest_throws_for_unsuccessful_upstream_response()
    {
        var provider = CreateProvider(_ => JsonResponse(HttpStatusCode.BadRequest, "{\"message\":\"invalid model\"}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "invalid",
            Prompt = "A test image"
        }));

        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageRequest_throws_when_response_contains_no_usable_images()
    {
        var provider = CreateProvider(_ => JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"index\":0}]}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "test-model",
            Prompt = "A test image"
        }));

        Assert.Contains("usable images", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAI_image_generation_routes_through_nebulablock_image_backend()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/v1/images/generation", request.RequestUri?.PathAndQuery);

            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("custom-image-model", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal("A blue nebula", payload.RootElement.GetProperty("prompt").GetString());
            Assert.Equal(640, payload.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(480, payload.RootElement.GetProperty("height").GetInt32());

            return JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"b64_json\":\"b3V0cHV0\"}]}" );
        });

        var response = await provider.OpenAIImageGenerationRequestAsync(new AIHappey.Core.Models.OpenAIImageGenerationRequest
        {
            Model = "custom-image-model",
            Prompt = "A blue nebula",
            Size = "640x480"
        });

        Assert.Equal("b3V0cHV0", response.Data?.Single().B64Json);
    }

    private static NebulaBlockProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
