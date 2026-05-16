using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Inworld;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Inworld;

public sealed class InworldProviderTests
{
    [Fact]
    public async Task ListModels_returns_api_models_catalog_entries_and_router_shortcuts()
    {
        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (path.StartsWith("/llm/v1alpha/models", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """
                    {
                      "models": [
                        {
                          "model": "gpt-4o",
                          "provider": "openai",
                          "modelCreator": "OpenAI",
                          "pricing": {
                            "prompt": 0.0000025,
                            "completion": 0.00001
                          },
                          "spec": {
                            "inputModalities": ["text", "image"],
                            "outputModalities": ["text"],
                            "contextLength": 128000,
                            "maxCompletionTokens": 16384,
                            "supportedParameters": ["max_tokens", "tools"],
                            "capabilities": {
                              "functionCalling": true,
                              "vision": true,
                              "promptCaching": true
                            }
                          },
                          "isSupported": true
                        }
                      ]
                    }
                    """);
            }

            if (path.StartsWith("/router/v1/routers", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """
                    {
                      "routers": [
                        {
                          "name": "my-router",
                          "displayName": "My Router",
                          "routes": [
                            {
                              "route": {
                                "route_id": "premium",
                                "variants": []
                              },
                              "condition": {
                                "cel_expression": "true"
                              }
                            }
                          ],
                          "defaultRoute": {
                            "route_id": "default",
                            "variants": []
                          }
                        }
                      ]
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var models = (await provider.ListModels()).ToList();

        var routedModel = Assert.Single(models, model => model.Id == "inworld/SERVICE_PROVIDER_OPENAI/gpt-4o");
        Assert.Equal("gpt-4o", routedModel.Name);
        Assert.Equal("OpenAI", routedModel.OwnedBy);
        Assert.Equal(128000, routedModel.ContextWindow);
        Assert.Equal(16384, routedModel.MaxTokens);
        Assert.Equal(0.0000025m, routedModel.Pricing?.Input);
        Assert.Equal(0.00001m, routedModel.Pricing?.Output);
        Assert.Contains("provider:openai", routedModel.Tags ?? []);
        Assert.Contains("tools", routedModel.Tags ?? []);
        Assert.Contains("vision", routedModel.Tags ?? []);

        Assert.Contains(models, model => model.Id == "inworld/auto");

        var routerModel = Assert.Single(models, model => model.Id == "inworld/my-router");
        Assert.Equal("My Router", routerModel.Name);
        Assert.Contains("router", routerModel.Tags ?? []);
        Assert.Contains("shortcut", routerModel.Tags ?? []);
        Assert.Contains("routing-policy", routerModel.Tags ?? []);
    }

    [Fact]
    public async Task CompleteChatAsync_rewrites_provider_shortcut_to_model_and_extra_body_provider()
    {
        JsonDocument? capturedBody = null;

        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/chat/completions")
            {
                capturedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                return JsonResponse(
                    """
                    {
                      "id": "chatcmpl-1",
                      "object": "chat.completion",
                      "created": 1,
                      "model": "openai/gpt-4o",
                      "choices": [],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "inworld/SERVICE_PROVIDER_OPENAI/gpt-4o",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello", JsonSerializerOptions.Web)
                }
            ]
        });

        Assert.NotNull(capturedBody);
        var root = capturedBody!.RootElement;
        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.Equal("SERVICE_PROVIDER_OPENAI", root.GetProperty("extra_body").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task CompleteChatAsync_preserves_router_shortcut_model()
    {
        JsonDocument? capturedBody = null;

        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/chat/completions")
            {
                capturedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                return JsonResponse(
                    """
                    {
                      "id": "chatcmpl-1",
                      "object": "chat.completion",
                      "created": 1,
                      "model": "inworld/my-router",
                      "choices": [],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "inworld/my-router",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello", JsonSerializerOptions.Web)
                }
            ]
        });

        Assert.NotNull(capturedBody);
        var root = capturedBody!.RootElement;
        Assert.Equal("inworld/my-router", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("extra_body", out _));
    }

    private static InworldProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new InworldProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient),
            new NullEndUserIdResolver());
    }

    private static InworldProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "inworld" ? "test-key" : null;
    }

    private sealed class NullEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
