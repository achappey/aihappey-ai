using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.LightOn;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.LightOn;

public sealed class LightOnProviderTests
{
    [Fact]
    public async Task ListModels_discovers_native_model_names_and_metadata()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("/api/v3/instance/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                [
                  {
                    "name": "mistral-large-latest",
                    "display_name": "Mistral Large",
                    "description": "Chat model",
                    "uuid": "model-1",
                    "is_custom": false
                  }
                ]
                """);
        });

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("lighton/mistral-large-latest", model.Id);
        Assert.Equal("Mistral Large", model.Name);
        Assert.Equal("lighton", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal("Chat model", model.Description);
        Assert.Equal("model-1", model.Metadata!["uuid"].GetString());
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListModels_skips_discovery_without_an_api_key()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException(), apiKey: null);

        Assert.Empty(await provider.ListModels());
    }

    [Fact]
    public async Task CompleteChat_uses_v3_chat_completions_and_forwards_native_options()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/v3/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("mistral-large-latest", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(3, body.RootElement.GetProperty("top_k").GetInt32());

            return JsonResponse("""
                {
                  "id": "completion-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "mistral-large-latest",
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
            Model = "mistral-large-latest",
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
            Assert.Equal("/api/v3/chat/completions", request.RequestUri!.AbsolutePath);
            const string events = """
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"mistral-large-latest","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

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
                           Model = "mistral-large-latest",
                           Stream = true
                       }))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("hello", updates[0].Choices[0].Delta.Content);
    }

    private static LightOnProvider CreateProvider(
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
