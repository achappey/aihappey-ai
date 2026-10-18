using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ModelScope;
using AIHappey.Gateway.HeaderAuth;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ModelScope;

public sealed class ModelScopeProviderTests
{
    [Fact]
    public void Provider_header_is_registered()
    {
        Assert.Equal("X-ModelScope-Key", HeaderApiKeyResolver.SupportedProviderHeaders["modelscope"]);
    }

    [Fact]
    public async Task ListModels_preserves_native_ids_and_metadata_and_caches_results()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("https://api-inference.modelscope.ai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {
                      "id": "Qwen/Qwen3-1.7B:DashScope",
                      "object": "model",
                      "created": 1686935002,
                      "owned_by": "Qwen",
                      "custom_metadata": { "supports_reasoning": true }
                    },
                    { "id": "zai-org/GLM-4.5" },
                    { "object": "model" }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal(2, models.Length);
        Assert.Equal("modelscope/Qwen/Qwen3-1.7B:DashScope", models[0].Id);
        Assert.Equal("Qwen/Qwen3-1.7B:DashScope", models[0].Name);
        Assert.Equal("Qwen", models[0].OwnedBy);
        Assert.Equal(1686935002, models[0].Created);
        Assert.Equal("language", models[0].Type);
        Assert.True(models[0].Metadata!["custom_metadata"].GetProperty("supports_reasoning").GetBoolean());
        Assert.Equal("modelscope/zai-org/GLM-4.5", models[1].Id);
        Assert.Equal("modelscope", models[1].OwnedBy);
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListModels_skips_discovery_without_an_api_key()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException(), apiKey: null);

        Assert.Empty(await provider.ListModels());
    }

    [Fact]
    public async Task CompleteChat_uses_openai_route_and_forwards_provider_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(
                "https://api-inference.modelscope.ai/v1/chat/completions",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("Qwen/Qwen3-1.7B:DashScope", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("top_k").GetInt32());

            return JsonResponse("""
                {
                  "id": "chatcmpl-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "Qwen/Qwen3-1.7B:DashScope",
                  "choices": [
                    {
                      "index": 0,
                      "message": { "role": "assistant", "content": "hello" },
                      "finish_reason": "stop"
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 2,
                    "completion_tokens": 1,
                    "total_tokens": 3
                  }
                }
                """);
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "Qwen/Qwen3-1.7B:DashScope",
            AdditionalProperties = new()
            {
                ["top_k"] = JsonSerializer.SerializeToElement(7)
            }
        });

        var result = JsonSerializer.SerializeToElement(response);
        Assert.Equal("hello", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(3, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_preserves_streamed_text()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(
                "https://api-inference.modelscope.ai/v1/chat/completions",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);

            const string events = """
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Qwen/Qwen3-1.7B:DashScope","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

                data: [DONE]

                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
            };
        });

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
                       {
                           Model = "Qwen/Qwen3-1.7B:DashScope",
                           Stream = true
                       }))
            updates.Add(update);

        Assert.Single(updates);
        var result = JsonSerializer.SerializeToElement(updates[0]);
        Assert.Equal("hello", result.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Responses_uses_chat_completion_translation()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal(
                "https://api-inference.modelscope.ai/v1/chat/completions",
                request.RequestUri!.AbsoluteUri);
            return JsonResponse("""
                {
                  "id": "chatcmpl-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "Qwen/Qwen3-1.7B:DashScope",
                  "choices": [
                    {
                      "index": 0,
                      "message": { "role": "assistant", "content": "hello" },
                      "finish_reason": "stop"
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 2,
                    "completion_tokens": 1,
                    "total_tokens": 3
                  }
                }
                """);
        });

        var response = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "Qwen/Qwen3-1.7B:DashScope",
            Input = "Hi"
        });

        Assert.Equal("completed", response.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StreamUnified_reports_trailing_usage()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Qwen/Qwen3-1.7B:DashScope","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: {"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

                data: [DONE]

                """, Encoding.UTF8, "text/event-stream")
        });

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(new()
                       {
                           ProviderId = "modelscope",
                           Model = "Qwen/Qwen3-1.7B:DashScope"
                       }))
            events.Add(item);

        var finish = Assert.IsType<AIFinishEventData>(
            Assert.Single(events, item => item.Event.Type == "finish").Event.Data);
        Assert.Equal(2, finish.InputTokens);
        Assert.Equal(3, finish.OutputTokens);
        Assert.Equal(5, finish.TotalTokens);
    }

    private static ModelScopeProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = "test-key")
        => new(
            new StaticApiKeyResolver(apiKey),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver(string? apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
