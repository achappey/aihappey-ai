using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Infermatic;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Infermatic;

public sealed class InfermaticProviderTests
{
    [Fact]
    public async Task ListModels_discovers_native_ids_and_maps_catalog_fields()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.totalgpt.ai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {
                      "id": "Sao10K-L3.3-70B-Euryale-v2.3-FP8-Dynamic",
                      "object": "model",
                      "created": 1677610602,
                      "owned_by": "openai",
                      "extra_metadata": "native"
                    }
                  ]
                }
                """);
        });

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("infermatic/Sao10K-L3.3-70B-Euryale-v2.3-FP8-Dynamic", model.Id);
        Assert.Equal("Sao10K-L3.3-70B-Euryale-v2.3-FP8-Dynamic", model.Name);
        Assert.Equal("openai", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal(1677610602, model.Created);
    }

    [Fact]
    public async Task ListModels_skips_discovery_without_an_api_key()
    {
        var provider = CreateProvider(
            _ => throw new InvalidOperationException("The discovery request should not be sent."),
            apiKey: null);

        Assert.Empty(await provider.ListModels());
    }

    [Fact]
    public async Task CompleteChat_uses_documented_endpoint_and_forwards_native_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.totalgpt.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("native-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("top_k").GetInt32());
            return JsonResponse("""
                {
                  "id": "chatcmpl-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "native-model",
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
            Model = "native-model",
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
            Assert.Equal("https://api.totalgpt.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            const string events = """
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"native-model","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

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
                           Model = "native-model",
                           Stream = true
                       }))
            updates.Add(update);

        Assert.Single(updates);
        var result = JsonSerializer.SerializeToElement(updates[0]);
        Assert.Equal("hello", result.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAIEmbedding_uses_native_endpoint_model_and_options_and_reports_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.totalgpt.ai/v1/embeddings", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("intfloat-multilingual-e5-base", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("native_option").GetBoolean());
            Assert.Equal(["hello"], body.RootElement.GetProperty("input").EnumerateArray()
                .Select(value => value.GetString()));
            return JsonResponse("""
                {
                  "object": "list",
                  "model": "intfloat-multilingual-e5-base",
                  "data": [
                    {
                      "object": "embedding",
                      "embedding": [0.1, 0.2, 0.3],
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

        var response = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "infermatic/intfloat-multilingual-e5-base",
            Input = JsonSerializer.SerializeToElement(new[] { "hello" }),
            AdditionalProperties = new()
            {
                ["native_option"] = JsonSerializer.SerializeToElement(true)
            }
        });

        Assert.Equal("infermatic/intfloat-multilingual-e5-base", response.Model);
        Assert.Equal(2, response.Usage.PromptTokens);
        Assert.Equal([0.1f, 0.2f, 0.3f], response.Data.Single().Embedding.Deserialize<float[]>()!);
    }

    [Fact]
    public async Task EmbeddingRequest_maps_values_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.totalgpt.ai/v1/embeddings", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("intfloat-multilingual-e5-base", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("native_option").GetBoolean());
            return JsonResponse("""
                {
                  "object": "list",
                  "model": "intfloat-multilingual-e5-base",
                  "data": [
                    {
                      "object": "embedding",
                      "embedding": [0.1, 0.2, 0.3],
                      "index": 0
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 4,
                    "total_tokens": 4
                  }
                }
                """);
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "infermatic/intfloat-multilingual-e5-base",
            Values = ["hello"],
            ProviderOptions = new()
            {
                ["infermatic"] = JsonSerializer.SerializeToElement(new
                {
                    native_option = true
                })
            }
        });

        Assert.Equal([0.1d, 0.2d, 0.3d], response.Embeddings.Single());
        Assert.Equal(4, response.Usage!.Tokens);
        Assert.NotNull(response.ProviderMetadata);
    }

    [Fact]
    public async Task EmbeddingRequest_propagates_provider_errors()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"unsupported model"}}""", Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
            {
                Model = "unsupported-model",
                Input = JsonSerializer.SerializeToElement("hello")
            }));

        Assert.Contains("unsupported model", exception.Message);
    }

    private static InfermaticProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = "test-key")
        => new(
            new StaticApiKeyResolver(apiKey),
            new StaticHttpClientFactory(new StaticResponseHandler(responder)),
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
