using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Clarifai;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Clarifai;

public sealed class ClarifaiProviderTests
{
    [Fact]
    public async Task ListModels_discovers_native_model_ids_and_metadata()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("https://api.clarifai.com/v2/ext/openai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {
                      "id": "user/model-one",
                      "object": "model",
                      "created": 1716768000,
                      "owned_by": "user",
                      "shutdown_date": null
                    },
                    {
                      "id": "vendor/model-two",
                      "owned_by": "vendor"
                    },
                    { "object": "model" }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToArray();
        var model = Assert.Single(models, item => item.Name == "user/model-one");

        Assert.Equal(2, models.Length);
        Assert.Equal("clarifai/user/model-one", model.Id);
        Assert.Equal("user", model.OwnedBy);
        Assert.Equal(1716768000, model.Created);
        Assert.Equal("language", model.Type);
        Assert.True(model.Metadata!.ContainsKey("shutdown_date"));
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
    public async Task CompleteChat_uses_openai_compatible_endpoint_and_forwards_native_options()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(
                "https://api.clarifai.com/v2/ext/openai/v1/chat/completions",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("user/model-one", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(3, body.RootElement.GetProperty("top_k").GetInt32());
            return JsonResponse("""
                {
                  "id": "completion-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "user/model-one",
                  "choices": [],
                  "usage": {
                    "prompt_tokens": 2,
                    "completion_tokens": 3,
                    "total_tokens": 5
                  }
                }
                """);
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "user/model-one",
            AdditionalProperties = new()
            {
                ["top_k"] = JsonSerializer.SerializeToElement(3)
            }
        });

        var result = JsonSerializer.SerializeToElement(response);
        Assert.Equal(5, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_preserves_streamed_text()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(
                "https://api.clarifai.com/v2/ext/openai/v1/chat/completions",
                request.RequestUri!.AbsoluteUri);
            const string events = """
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"user/model-one","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

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
                           Model = "user/model-one",
                           Stream = true
                       }))
            updates.Add(update);

        Assert.Single(updates);
        var serializedUpdate = JsonSerializer.SerializeToElement(updates[0]);
        Assert.Equal(
            "hello",
            serializedUpdate.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
    }

    private static ClarifaiProvider CreateProvider(
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
