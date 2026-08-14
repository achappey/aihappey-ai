using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.RoutePlex;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.RoutePlex;

public sealed class RoutePlexProviderTests
{
    private static readonly string[] SmartModelIds =
    [
        "routeplex/routeplex-ai",
        "routeplex/routeplex-ai-cost",
        "routeplex/routeplex-ai-speed",
        "routeplex/routeplex-ai-quality",
        "routeplex/routeplex-ai-balanced"
    ];

    [Fact]
    public async Task ListModels_prepends_smart_models_and_deduplicates_remote_router_model()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/v1/models", request.RequestUri?.AbsolutePath);
            return JsonResponse("""
            {
              "data": [
                { "id": "routeplex-ai", "display_name": "Remote duplicate" },
                { "id": "gpt-4o-mini", "display_name": "GPT-4o mini" }
              ]
            }
            """);
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Equal(SmartModelIds, models.Take(SmartModelIds.Length).Select(model => model.Id));
        Assert.Single(models, model => model.Id == "routeplex/routeplex-ai");
        Assert.Equal("routeplex/gpt-4o-mini", models[SmartModelIds.Length].Id);
    }

    [Theory]
    [InlineData("routeplex/routeplex-ai", "routeplex-ai", null)]
    [InlineData("routeplex-ai-cost", "routeplex-ai", "cost")]
    [InlineData("routeplex/routeplex-ai-speed", "routeplex-ai", "speed")]
    [InlineData("routeplex-ai-quality", "routeplex-ai", "quality")]
    [InlineData("routeplex/routeplex-ai-balanced", "routeplex-ai", "balanced")]
    [InlineData("routeplex/gpt-4o-mini", "routeplex/gpt-4o-mini", null)]
    public async Task CompleteChatAsync_translates_smart_shortcuts(
        string requestedModel,
        string expectedModel,
        string? expectedStrategy)
    {
        JsonDocument? capturedPayload = null;
        string? capturedStrategy = null;

        var provider = CreateProvider(async request =>
        {
            Assert.Equal("/api/v1/chat/completions", request.RequestUri?.AbsolutePath);
            capturedPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            capturedStrategy = request.Headers.TryGetValues("X-RoutePlex-Strategy", out var values)
                ? Assert.Single(values)
                : null;

            return JsonResponse("""
            {
              "id": "chatcmpl-test",
              "object": "chat.completion",
              "created": 1,
              "model": "gpt-4o-mini",
              "choices": [],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """);
        });

        await provider.CompleteChatAsync(CreateOptions(requestedModel));

        Assert.NotNull(capturedPayload);
        Assert.Equal(expectedModel, capturedPayload!.RootElement.GetProperty("model").GetString());
        Assert.Equal(expectedStrategy, capturedStrategy);
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_applies_fixed_strategy_to_streaming_request()
    {
        JsonDocument? capturedPayload = null;
        string? capturedStrategy = null;

        var provider = CreateProvider(async request =>
        {
            Assert.Equal("/api/v1/chat/completions", request.RequestUri?.AbsolutePath);
            capturedPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            capturedStrategy = Assert.Single(request.Headers.GetValues("X-RoutePlex-Strategy"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });

        await foreach (var _ in provider.CompleteChatStreamingAsync(CreateOptions("routeplex-ai-quality")))
        {
        }

        Assert.NotNull(capturedPayload);
        Assert.Equal("routeplex-ai", capturedPayload!.RootElement.GetProperty("model").GetString());
        Assert.True(capturedPayload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("quality", capturedStrategy);
    }

    private static ChatCompletionOptions CreateOptions(string model)
        => new()
        {
            Model = model,
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello")
                }
            ]
        };

    private static RoutePlexProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static RoutePlexProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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
        public string? Resolve(string provider) => provider == "routeplex" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
