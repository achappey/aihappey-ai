using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.MangoBoost;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.MangoBoost;

public sealed class MangoBoostProviderTests
{
    [Fact]
    public async Task ListModels_discovers_native_ids_and_caches_by_credential()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("https://api.mangoboost.io/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    { "id": "zai-org/GLM-5.3", "created": 123, "owned_by": "zai-org" },
                    { "id": "other/model" }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToArray();
        var model = models[0];

        Assert.Equal(2, models.Length);
        Assert.Equal("mangoboost/zai-org/GLM-5.3", model.Id);
        Assert.Equal("zai-org/GLM-5.3", model.Name);
        Assert.Equal("zai-org", model.OwnedBy);
        Assert.Equal(123, model.Created);
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
    public async Task CompleteChat_uses_openai_route_and_forwards_native_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.mangoboost.io/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("zai-org/GLM-5.3", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("top_k").GetInt32());
            return JsonResponse("""
                {
                  "id": "chatcmpl-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "zai-org/GLM-5.3",
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
            Model = "zai-org/GLM-5.3",
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
            Assert.Equal("https://api.mangoboost.io/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            const string events = """
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"zai-org/GLM-5.3","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

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
                           Model = "zai-org/GLM-5.3",
                           Stream = true
                       }))
            updates.Add(update);

        Assert.Single(updates);
        var result = JsonSerializer.SerializeToElement(updates[0]);
        Assert.Equal("hello", result.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
    }

    private static MangoBoostProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = "test-key")
        => new(
            new StaticApiKeyResolver(apiKey),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new StaticResponseHandler(responder)));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver(string? apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
