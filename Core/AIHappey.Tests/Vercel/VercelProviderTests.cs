using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Vercel;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Vercel;

public sealed class VercelProviderTests
{
    [Fact]
    public async Task ListModels_discovers_chat_models_and_maps_native_ids_and_context_window()
    {
        string? path = null;
        string? authorization = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            authorization = request.Headers.Authorization?.ToString();
            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {
                      "id": "anthropic/claude-sonnet-4",
                      "name": "Claude Sonnet 4",
                      "owned_by": "anthropic",
                      "type": "language",
                      "context_window": 200000,
                      "supported_parameters": ["temperature", "tools"]
                    },
                    {
                      "id": "openai/dall-e-3",
                      "type": "image-generation"
                    },
                    {
                      "id": "openai/gpt-4o-mini",
                      "type": "text-generation"
                    }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Equal("/v1/models", path);
        Assert.Equal("Bearer " + "test" + "-key", authorization);
        Assert.Collection(models,
            model =>
            {
                Assert.Equal("vercel/anthropic/claude-sonnet-4", model.Id);
                Assert.Equal("Claude Sonnet 4", model.Name);
                Assert.Equal("anthropic", model.OwnedBy);
                Assert.Equal("language", model.Type);
                Assert.Equal(200000, model.ContextWindow);
            },
            model =>
            {
                Assert.Equal("vercel/openai/gpt-4o-mini", model.Id);
                Assert.Equal("text-generation", model.Type);
            });
    }

    [Fact]
    public async Task CompleteChat_forwards_gateway_model_and_native_request_fields()
    {
        string? body = null;
        string? path = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {
                  "id": "completion-1",
                  "object": "chat.completion",
                  "created": 1710000000,
                  "model": "anthropic/claude-sonnet-4",
                  "choices": [],
                  "usage": { "prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5 }
                }
                """);
        });

        using var nativeOptions = JsonDocument.Parse("""{"gateway":{"order":["vertex","anthropic"]}}""");
        var completion = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "anthropic/claude-sonnet-4",
            AdditionalProperties = new()
            {
                ["providerOptions"] = nativeOptions.RootElement.Clone()
            }
        });

        Assert.Equal("/v1/chat/completions", path);
        using var request = JsonDocument.Parse(body!);
        Assert.Equal("anthropic/claude-sonnet-4", request.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "anthropic",
            request.RootElement.GetProperty("providerOptions")
                .GetProperty("gateway").GetProperty("order")[1].GetString());
        using var usage = JsonDocument.Parse(JsonSerializer.Serialize(completion.Usage));
        Assert.Equal(5, usage.RootElement.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_preserves_gateway_model_and_stream_usage()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"id":"completion-1","object":"chat.completion.chunk","created":1710000000,"model":"anthropic/claude-sonnet-4","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

                data: {"id":"completion-1","object":"chat.completion.chunk","created":1710000000,"model":"anthropic/claude-sonnet-4","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        });

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "anthropic/claude-sonnet-4",
            Stream = true
        }))
            updates.Add(update);

        Assert.Equal("vercel/anthropic/claude-sonnet-4", updates[0].Model);
        using var usage = JsonDocument.Parse(JsonSerializer.Serialize(updates[1].Usage));
        Assert.Equal(5, usage.RootElement.GetProperty("total_tokens").GetInt32());
    }

    private static VercelProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new Handler(responder));
        return new VercelProvider(
            new ApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new HttpClientFactory(client));
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class ApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "vercel" ? "test-key" : null;
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
