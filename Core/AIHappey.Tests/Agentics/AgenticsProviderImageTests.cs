using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Agentics;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Agentics;

public sealed class AgenticsProviderImageTests
{
    [Fact]
    public async Task ImageRequest_posts_generation_payload_and_normalizes_data_url_response()
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
                {"created":1730000000,"model":"Juggernaut","data":[{"revised_prompt":"A robot","url":"data:image/png;base64,aVZCT1J3MEtHZ29BQUE="}]}
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            };
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "Juggernaut",
            Prompt = "A robot",
            Size = "1024x1024",
            N = 1,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["agentics"] = JsonSerializer.SerializeToElement(new
                {
                    negative_prompt = "blur",
                    style = "cinematic"
                }, JsonSerializerOptions.Web)
            },
            Files =
            [
                new ImageFile
                {
                    MediaType = MediaTypeNames.Image.Png,
                    Data = "aW1hZ2U="
                }
            ]
        });

        Assert.Equal("/v1/images/generations", requestedPath);
        Assert.Contains("\"model\":\"Juggernaut\"", requestJson);
        Assert.Contains("\"prompt\":\"A robot\"", requestJson);
        Assert.Contains("\"width\":1024", requestJson);
        Assert.Contains("\"height\":1024", requestJson);
        Assert.Contains("\"format\":\"b64_json\"", requestJson);
        Assert.Contains("\"negative_prompt\":\"blur\"", requestJson);
        Assert.Contains("\"style\":\"cinematic\"", requestJson);
        Assert.Equal(["data:image/png;base64,aVZCT1J3MEtHZ29BQUE="], result.Images?.ToArray() ?? []);
        Assert.Equal("Juggernaut", result.Response.ModelId);
        Assert.Contains(result.Warnings, warning => JsonSerializer.Serialize(warning).Contains("files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListModels_includes_image_models_from_images_endpoint()
    {
        var provider = CreateProvider(request => request.RequestUri?.PathAndQuery switch
        {
            "/v1/models" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"data":[{"id":"llama-3","owned_by":"agentics","display_name":"Llama 3"}]}
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            },
            "/v1/images/models" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"data":[{"category":"General","description":"Powerful versatile model","internal":false,"name":"Juggernaut","provider":"arta"}]}
                """, Encoding.UTF8, MediaTypeNames.Application.Json)
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var models = (await provider.ListModels()).ToList();
        var imageModel = models.Single(model => model.Name == "Juggernaut");

        Assert.Equal("agentics/Juggernaut", imageModel.Id);
        Assert.Equal("image", imageModel.Type);
        Assert.Equal("arta", imageModel.OwnedBy);
        Assert.Equal("Powerful versatile model", imageModel.Description);
        Assert.Contains("General", imageModel.Tags ?? []);
        Assert.Contains("arta", imageModel.Tags ?? []);
    }

    private static AgenticsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new AgenticsProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
