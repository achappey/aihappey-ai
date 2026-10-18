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
    public async Task ListModels_discovers_enabled_language_and_embedding_models_from_documented_schema()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("https://api.lighton.ai/api/v3/instance/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                [
                  {
                    "technical_name": "mistral-large-latest",
                    "name": "Mistral Large",
                    "description": "Chat model",
                    "model_type": "Large Language Model",
                    "enabled": true
                  },
                  {
                    "technical_name": "modern-bert",
                    "name": "ModernBERT",
                    "model_type": "Embedding Model",
                    "enabled": true
                  },
                  {
                    "technical_name": "disabled-model",
                    "name": "Disabled model",
                    "model_type": "Large Language Model",
                    "enabled": false
                  },
                  {
                    "technical_name": "vision-language-model",
                    "name": "Vision Language Model",
                    "model_type": "Vision Language Model",
                    "enabled": true
                  },
                  {
                    "technical_name": "multi-vector-model",
                    "name": "Multi-Vector Model",
                    "model_type": "Multi-Vector Model",
                    "enabled": true
                  }
                ]
                """);
        });

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(3, models.Length);
        var model = models[0];

        Assert.Equal("lighton/mistral-large-latest", model.Id);
        Assert.Equal("Mistral Large", model.Name);
        Assert.Equal("lighton", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal("Chat model", model.Description);
        Assert.DoesNotContain("enabled", model.Metadata!.Keys);
        Assert.Equal("embedding", models[1].Type);
        Assert.Equal("lighton/modern-bert", models[1].Id);
        Assert.Equal("language", models[2].Type);
        Assert.Equal("lighton/vision-language-model", models[2].Id);
        Assert.Equal(3, (await provider.ListModels()).Count());
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
            Assert.Equal("https://api.lighton.ai/api/v3/chat/completions", request.RequestUri!.AbsoluteUri);
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
            Assert.Equal("https://api.lighton.ai/api/v3/chat/completions", request.RequestUri!.AbsoluteUri);
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
        var serializedUpdate = JsonSerializer.SerializeToElement(updates[0]);
        Assert.Equal("hello", serializedUpdate.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAIEmbedding_uses_native_endpoint_and_preserves_native_options()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.lighton.ai/api/v3/embeddings", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("modern-bert", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(["hello"], body.RootElement.GetProperty("input").EnumerateArray()
                .Select(value => value.GetString()));
            Assert.Equal("native-value", body.RootElement.GetProperty("native_option").GetString());

            return JsonResponse("""
                {
                  "object": "list",
                  "model": "modern-bert",
                  "data": [
                    {
                      "object": "embedding",
                      "embedding": [0.1, 0.2],
                      "index": 0
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 2,
                    "total_tokens": 2
                  }
                }
                """);
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = "modern-bert",
            Input = JsonSerializer.SerializeToElement(new[] { "hello" }),
            AdditionalProperties = new()
            {
                ["native_option"] = JsonSerializer.SerializeToElement("native-value")
            }
        });

        Assert.Equal("lighton/modern-bert", response.Model);
        Assert.Equal(2, response.Usage.PromptTokens);
        Assert.Equal([0.1f, 0.2f], response.Data.Single().Embedding.Deserialize<float[]>()!);
    }

    [Fact]
    public async Task EmbeddingRequest_uses_gateway_mapping_and_reports_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.lighton.ai/api/v3/embeddings", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("modern-bert", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(["hello"], body.RootElement.GetProperty("input").EnumerateArray()
                .Select(value => value.GetString()));
            Assert.True(body.RootElement.GetProperty("native_option").GetBoolean());

            return JsonResponse("""
                {
                  "object": "list",
                  "model": "modern-bert",
                  "data": [
                    {
                      "object": "embedding",
                      "embedding": [0.1, 0.2],
                      "index": 0
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 2,
                    "total_tokens": 2
                  }
                }
                """);
        });

        var response = await provider.EmbeddingRequestAsync(new()
        {
            Model = "lighton/modern-bert",
            Values = ["hello"],
            ProviderOptions = new()
            {
                ["lighton"] = JsonSerializer.SerializeToElement(new { native_option = true })
            }
        });

        Assert.Equal([0.1f, 0.2f], response.Embeddings.Single());
        Assert.Equal(2, response.Usage!.Tokens);
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
