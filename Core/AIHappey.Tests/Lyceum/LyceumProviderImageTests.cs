using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Lyceum;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Lyceum;

public sealed class LyceumProviderImageTests
{
    [Fact]
    public async Task ImageRequest_posts_generation_payload_and_downloads_image_url()
    {
        var requestedPath = string.Empty;
        var requestJson = string.Empty;
        var imageBytes = Encoding.UTF8.GetBytes("png-bytes");

        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.PathAndQuery == "/api/v2/external/images/generations")
            {
                requestedPath = request.RequestUri.PathAndQuery;
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {"image_url":"https://cdn.lyceum.test/generated.png"}
                    """, Encoding.UTF8, MediaTypeNames.Application.Json)
                };
            }

            if (request.RequestUri?.AbsoluteUri == "https://cdn.lyceum.test/generated.png")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MediaTypeNames.Image.Png) }
                    }
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "lyc-image-ultra",
            Prompt = "A robot",
            AspectRatio = "16:9",
            Seed = 42,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["lyceum"] = JsonSerializer.SerializeToElement(new
                {
                    aspect_ratio = "4:3",
                    custom_option = "raw"
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.Equal("/api/v2/external/images/generations", requestedPath);
        Assert.Contains("\"model\":\"lyc-image-ultra\"", requestJson);
        Assert.Contains("\"prompt\":\"A robot\"", requestJson);
        Assert.Contains("\"aspect_ratio\":\"4:3\"", requestJson);
        Assert.Contains("\"custom_option\":\"raw\"", requestJson);
        Assert.Equal([$"data:image/png;base64,{Convert.ToBase64String(imageBytes)}"], result.Images?.ToArray() ?? []);
        Assert.Equal("lyceum/lyc-image-ultra", result.Response.ModelId);
        Assert.Contains(result.Warnings, warning => JsonSerializer.Serialize(warning).Contains("seed", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.ProviderMetadata?.ContainsKey("lyceum"));
    }

    [Fact]
    public async Task ListModels_merges_chat_and_image_models()
    {
        var provider = CreateProvider(request => request.RequestUri?.PathAndQuery switch
        {
            "/api/v2/external/serverless/models" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"data":[{"id":"llama-3","owned_by":"lyceum","created":1730000000}]}
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            },
            "/api/v2/external/images/models" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                [
                    {"id":"lyc-image-ultra","name":"Image Ultra"},
                    {"id":"lyc-flux-1-dev","name":"FLUX.1 Dev"}
                ]
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var models = (await provider.ListModels()).ToList();
        var chatModel = models.Single(model => model.Id == "lyceum/llama-3");
        var imageModel = models.Single(model => model.Id == "lyceum/lyc-image-ultra");

        Assert.Equal("language", chatModel.Type);
        Assert.Equal("Image Ultra", imageModel.Name);
        Assert.Equal("image", imageModel.Type);
        Assert.Equal(nameof(Lyceum), imageModel.OwnedBy);
    }

    private static LyceumProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new LyceumProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
