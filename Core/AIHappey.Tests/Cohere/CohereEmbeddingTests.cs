using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cohere;

public sealed class CohereEmbeddingTests
{
    [Fact]
    public async Task Image_embedding_uses_cohere_native_images_input()
    {
        JsonElement? capturedBody = null;
        var provider = CreateProvider(request => CaptureEmbeddingRequest(request, body => capturedBody = body));

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "embed-v5.0-pro",
            Values = [],
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cohere"] = JsonSerializer.SerializeToElement(new
                {
                    inputType = "image",
                    images = new[] { "data:image/png;base64,aW1hZ2U=" }
                })
            }
        });

        Assert.NotNull(capturedBody);
        Assert.Equal("image", capturedBody.Value.GetProperty("input_type").GetString());
        Assert.Equal("data:image/png;base64,aW1hZ2U=", capturedBody.Value.GetProperty("images")[0].GetString());
        Assert.False(capturedBody.Value.TryGetProperty("texts", out _));
        Assert.Single(response.Embeddings);
        Assert.Equal(12, response.Usage.Tokens);
    }

    [Fact]
    public async Task Fused_multimodal_embedding_uses_cohere_native_inputs()
    {
        JsonElement? capturedBody = null;
        var provider = CreateProvider(request => CaptureEmbeddingRequest(request, body => capturedBody = body));

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "embed-v5.0-pro",
            Values = [],
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cohere"] = JsonSerializer.SerializeToElement(new
                {
                    inputType = "search_document",
                    inputs = new[]
                    {
                        new
                        {
                            content = new object[]
                            {
                                new { type = "text", text = "A cat" },
                                new { type = "image_url", image_url = new { url = "data:image/png;base64,aW1hZ2U=" } }
                            }
                        }
                    }
                })
            }
        });

        Assert.NotNull(capturedBody);
        Assert.Equal("search_document", capturedBody.Value.GetProperty("input_type").GetString());
        Assert.Equal("A cat", capturedBody.Value.GetProperty("inputs")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(capturedBody.Value.TryGetProperty("texts", out _));
        Assert.False(capturedBody.Value.TryGetProperty("images", out _));
        Assert.Single(response.Embeddings);
        Assert.Equal(12, response.Usage.Tokens);
    }

    [Fact]
    public async Task Image_input_type_requires_native_images_input()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
            {
                Model = "embed-v5.0-pro",
                Input = JsonSerializer.SerializeToElement("A cat"),
                AdditionalProperties = new Dictionary<string, JsonElement>
                {
                    ["input_type"] = JsonSerializer.SerializeToElement("image")
                }
            }));

        Assert.Contains("images", exception.Message);
    }

    private static CohereProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.cohere.com/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new CohereProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static HttpResponseMessage CaptureEmbeddingRequest(
        HttpRequestMessage request,
        Action<JsonElement> capture)
    {
        Assert.Equal("/v2/embed", request.RequestUri?.AbsolutePath);
        using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        capture(document.RootElement.Clone());

        return JsonResponse("""
            {
              "embeddings": { "float": [[0.1, 0.2]] },
              "meta": { "billed_units": { "input_tokens": 12 } }
            }
            """);
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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
