using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Haimaker;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Haimaker;

public class HaimakerProviderModelsTests
{
    private const string Catalog = """
    [
      {
        "model_group": "openai/chat-latest",
        "providers": ["openai"],
        "max_input_tokens": 400000.0,
        "max_output_tokens": 128000.0,
        "input_cost_per_token": 0.000005,
        "output_cost_per_token": 0.00003,
        "mode": "responses",
        "supports_vision": true,
        "supports_web_search": true,
        "custom_upstream_property": { "preserved": true }
      }
    ]
    """;

    [Fact]
    public async Task ListModels_maps_model_hub_and_caches_original_shape()
    {
        var calls = 0;
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(memoryCache, request =>
        {
            calls++;
            Assert.Equal("https://api.haimaker.ai/public/model_hub", request.RequestUri?.ToString());
            return Json(Catalog);
        });

        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("haimaker/openai/chat-latest", model.Id);
        Assert.Equal("openai/chat-latest", model.Name);
        Assert.Equal("openai", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal(400000, model.ContextWindow);
        Assert.Equal(128000, model.MaxTokens);
        Assert.Equal(0.000005m, model.Pricing?.Input);
        Assert.Equal(0.00003m, model.Pricing?.Output);
        Assert.Contains("vision", model.Tags!);
        Assert.Contains("web-search", model.Tags!);

        await provider.ListModels();
        Assert.Equal(1, calls);

        Assert.True(memoryCache.TryGetValue(
            "models:haimaker:raw",
            out IReadOnlyList<JsonElement>? originals));
        var original = Assert.Single(originals!);
        Assert.True(original.GetProperty("custom_upstream_property").GetProperty("preserved").GetBoolean());
    }

    [Theory]
    [InlineData("responses", true)]
    [InlineData("RESPONSES", true)]
    [InlineData("chat", false)]
    [InlineData("unknown", false)]
    public void UsesResponsesMode_routes_only_responses_mode(string mode, bool expected)
    {
        using var document = JsonDocument.Parse($$"""{"mode":"{{mode}}"}""");
        Assert.Equal(expected, HaimakerProvider.UsesResponsesMode(document.RootElement.Clone()));
    }

    [Fact]
    public void UsesResponsesMode_defaults_to_chat_when_mode_is_missing()
    {
        using var document = JsonDocument.Parse("{}");
        Assert.False(HaimakerProvider.UsesResponsesMode(document.RootElement.Clone()));
        Assert.False(HaimakerProvider.UsesResponsesMode(null));
    }

    private static HaimakerProvider CreateProvider(
        IMemoryCache memoryCache,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new Handler(responder));
        return new HaimakerProvider(
            new ApiKeyResolver(),
            new AsyncCacheHelper(memoryCache),
            new HttpClientFactory(client));
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class ApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
