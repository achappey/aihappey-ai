using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AlphaNeural;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AlphaNeural;

public sealed class AlphaNeuralProviderImageTests
{
    [Fact]
    public void BuildAlphaNeuralImagePayload_omits_response_format_for_gpt_image_models()
    {
        var request = new ImageRequest
        {
            Model = "gpt-image-1",
            Prompt = "A tiny robot making espresso",
            Size = "1024x1024",
            N = 1,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["alphaneural"] = JsonSerializer.SerializeToElement(new
                {
                    quality = "high",
                    style = "vivid",
                    response_format = "url",
                    user = "end-user-1"
                }, JsonSerializerOptions.Web)
            }
        };

        var payload = AlphaNeuralProvider.BuildAlphaNeuralImagePayload(request);

        Assert.Equal("gpt-image-1", payload["model"]);
        Assert.Equal("A tiny robot making espresso", payload["prompt"]);
        Assert.Equal("1024x1024", payload["size"]);
        Assert.Equal(1, payload["n"]);
        Assert.Equal("high", payload["quality"]);
        Assert.Equal("vivid", payload["style"]);
        Assert.Equal("end-user-1", payload["user"]);
        Assert.False(payload.ContainsKey("response_format"));
    }

    [Fact]
    public void BuildAlphaNeuralImagePayload_forwards_response_format_for_non_gpt_image_models()
    {
        var request = new ImageRequest
        {
            Model = "dall-e-3",
            Prompt = "A minimal owl logo",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["alphaneural"] = JsonSerializer.SerializeToElement(new
                {
                    response_format = "b64_json"
                }, JsonSerializerOptions.Web)
            }
        };

        var payload = AlphaNeuralProvider.BuildAlphaNeuralImagePayload(request);

        Assert.Equal("b64_json", payload["response_format"]);
    }

    [Fact]
    public async Task ImageRequest_normalizes_b64_json_response_to_data_url()
    {
        var requestedPath = string.Empty;
        var requestJson = string.Empty;
        var provider = CreateProvider(request =>
        {
            requestedPath = request.RequestUri?.PathAndQuery ?? string.Empty;
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"created":1730000000,"data":[{"b64_json":"aVZCT1J3MEtHZ29BQUE="}]}
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            };
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "gpt-image-1",
            Prompt = "A cute baby sea otter in a knitted hat",
            Size = "1024x1024",
            N = 1
        });

        Assert.Equal("/v1/images/generations", requestedPath);
        Assert.DoesNotContain("response_format", requestJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["data:image/png;base64,aVZCT1J3MEtHZ29BQUE="], result.Images?.ToArray() ?? []);
        Assert.Equal("gpt-image-1", result.Response.ModelId);
    }

    [Fact]
    public async Task ListModels_marks_image_models_as_image_type()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"data":[{"id":"gpt-image-1","owned_by":"alphaneural"},{"id":"chat-model","owned_by":"alphaneural"}]}
            """, Encoding.UTF8, MediaTypeNames.Application.Json)
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Equal("image", models.Single(m => m.Name == "gpt-image-1").Type);
        Assert.Equal("language", models.Single(m => m.Name == "chat-model").Type);
    }

    private static AlphaNeuralProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new AlphaNeuralProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

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
