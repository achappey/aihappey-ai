using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Occludra;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Occludra;

public sealed class OccludraProviderImageTests
{
    [Fact]
    public async Task ImageRequestPassesRawProviderOptionsAndReturnsRawMetadata()
    {
        string? requestJson = null;
        AuthenticationHeaderValue? authorization = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/images/generations", request.RequestUri?.PathAndQuery);
            authorization = request.Headers.Authorization;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
            {
              "created": 1748372400,
              "x_request_id": "req_image",
              "aisg_metadata": { "provider_selected": "openai", "media_events": 1 },
              "data": [{ "b64_json": "occludra-base64" }]
            }
            """);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "dall-e-3",
            Prompt = "a secure fox",
            N = 1,
            Size = "1024x1024",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["occludra"] = JsonSerializer.SerializeToElement(new
                {
                    quality = "hd",
                    custom_flag = true,
                    nested = new { value = 7 }
                })
            }
        });

        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("test-key", authorization?.Parameter);
        using var requestDocument = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        var requestRoot = requestDocument.RootElement;
        Assert.Equal("dall-e-3", requestRoot.GetProperty("model").GetString());
        Assert.Equal("a secure fox", requestRoot.GetProperty("prompt").GetString());
        Assert.Equal("b64_json", requestRoot.GetProperty("response_format").GetString());
        Assert.Equal("hd", requestRoot.GetProperty("quality").GetString());
        Assert.True(requestRoot.GetProperty("custom_flag").GetBoolean());
        Assert.Equal(7, requestRoot.GetProperty("nested").GetProperty("value").GetInt32());
        Assert.Equal(["data:image/png;base64,occludra-base64"], result.Images);
        Assert.Equal("req_image", Assert.Contains("occludra", result.ProviderMetadata ?? [])
            .GetProperty("x_request_id").GetString());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1748372400).UtcDateTime, result.Response.Timestamp);
    }

    [Fact]
    public async Task ImageRequestDownloadsUrlResponseAsDataUrl()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsoluteUri == "https://images.example/generated.webp")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("image/webp") }
                    }
                };
            }

            return JsonResponse("""
            { "created": 1, "data": [{ "url": "https://images.example/generated.webp" }] }
            """);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "dall-e-3",
            Prompt = "url response"
        });

        Assert.Equal(["data:image/webp;base64,AQID"], result.Images);
    }

    [Fact]
    public async Task OpenAIGenerationPassesAdditionalPropertiesAtTopLevel()
    {
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
            { "created": 2, "data": [{ "b64_json": "openai-base64" }] }
            """);
        });

        var result = await provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = "dall-e-3",
            Prompt = "raw fields",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["x_provider"] = JsonSerializer.SerializeToElement("openai"),
                ["vendor_options"] = JsonSerializer.SerializeToElement(new { safety = "strict" })
            }
        });

        using var document = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        Assert.Equal("openai", document.RootElement.GetProperty("x_provider").GetString());
        Assert.Equal("strict", document.RootElement.GetProperty("vendor_options").GetProperty("safety").GetString());
        Assert.False(document.RootElement.TryGetProperty("providerMetadata", out _));
        Assert.Equal("openai-base64", result.Data!.Single().B64Json);
    }

    [Fact]
    public async Task OpenAIGenerationStreamingAdaptsNonStreamingResponse()
    {
        var provider = CreateProvider(_ => JsonResponse("""
        {
          "created": 3,
          "data": [{ "b64_json": "stream-base64" }],
          "usage": { "input_tokens": 1, "output_tokens": 2, "total_tokens": 3 }
        }
        """));

        var events = new List<IOpenAIImageStreamEvent>();
        await foreach (var streamEvent in provider.OpenAIImageGenerationStreamingAsync(new OpenAIImageGenerationRequest
        {
            Model = "dall-e-3",
            Prompt = "adapt response"
        }))
        {
            events.Add(streamEvent);
        }

        var completed = Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.Equal("stream-base64", completed.B64Json);
        Assert.Equal(3, completed.Usage?.TotalTokens);
    }

    [Fact]
    public async Task ImageEditsAreExplicitlyUnsupported()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No HTTP request expected."));
        var request = new OpenAIImageEditRequest { Model = "dall-e-3", Prompt = "edit" };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(request));
        Assert.Throws<NotSupportedException>(() => provider.OpenAIImageEditStreamingAsync(request));
    }

    private static OccludraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
