using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Clarifai;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Clarifai;

public sealed class ClarifaiProviderTests
{
    [Fact]
    public async Task ListModels_uses_native_paged_discovery_and_preserves_model_types()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal($"https://api.clarifai.com/v2/models?page={calls}", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Key", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return calls == 1 ? JsonResponse("""
                {
                  "models": [
                    {
                      "model_id": "model-id-one",
                      "name": "model-one",
                      "user_id": "user",
                      "app_id": "app",
                      "model_type_id": "text-to-text",
                      "description": "Text model",
                      "model_version": {
                        "method_signatures": [
                          {"name": "openai_transport", "method_type": "UNARY_UNARY"},
                          {"name": "openai_stream_transport", "method_type": "UNARY_STREAMING"}
                        ]
                      }
                    },
                    {
                      "model_id": "model-id-two",
                      "name": "image-model",
                      "user_id": "vendor",
                      "app_id": "images",
                      "model_type_id": "text-to-image",
                      "model_version": {
                        "method_signatures": [
                          {"name": "openai_transport"},
                          {"name": "openai_stream_transport"}
                        ]
                      }
                    },
                    {
                      "model_id": "model-id-three",
                      "name": "embed-model",
                      "user_id": "vendor",
                      "app_id": "embeddings",
                      "model_type_id": "text-embedder",
                      "model_version": {
                        "method_signatures": [
                          {"name": "openai_transport"},
                          {"name": "openai_stream_transport"}
                        ]
                      }
                    },
                    {
                      "model_id": "unsupported",
                      "name": "native-only",
                      "user_id": "vendor",
                      "app_id": "app",
                      "model_type_id": "visual-classifier"
                    }
                  ]
                }
                """) : JsonResponse("""{"status":{"code":10000,"description":"OK"}}""");
        });

        var models = (await provider.ListModels()).ToArray();
        var model = Assert.Single(models, item => item.Name == "model-one");

        Assert.Equal(3, models.Length);
        Assert.Equal("clarifai/https://clarifai.com/user/app/models/model-one", model.Id);
        Assert.Equal("user", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal("image", Assert.Single(models, item => item.Name == "image-model").Type);
        Assert.Equal("embedding", Assert.Single(models, item => item.Name == "embed-model").Type);
        Assert.Equal(3, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
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

    [Fact]
    public async Task Responses_uses_documented_endpoint_and_forwards_native_options()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.clarifai.com/v2/ext/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(2, body.RootElement.GetProperty("top_k").GetInt32());
            return JsonResponse("""
                {
                  "id": "resp-1",
                  "object": "response",
                  "created_at": 1716768000,
                  "status": "completed",
                  "model": "https://clarifai.com/user/app/models/model-one",
                  "output": []
                }
                """);
        });

        var response = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "https://clarifai.com/user/app/models/model-one",
            AdditionalProperties = new()
            {
                ["top_k"] = JsonSerializer.SerializeToElement(2)
            }
        });

        Assert.Equal("resp-1", response.Id);
    }

    [Fact]
    public async Task ResponsesStreaming_uses_documented_streaming_endpoint()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.clarifai.com/v2/ext/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            const string events = """
                event: response.created
                data: {"type":"response.created","sequence_number":1,"response":{"id":"resp-1","object":"response","created_at":1716768000,"status":"in_progress","model":"https://clarifai.com/user/app/models/model-one","output":[]}}

                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
            };
        });

        var parts = new List<ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new ResponseRequest
                       {
                           Model = "https://clarifai.com/user/app/models/model-one",
                           Stream = true
                       }))
            parts.Add(part);

        Assert.IsType<ResponseCreated>(Assert.Single(parts));
    }

    [Fact]
    public async Task ImageGeneration_forwards_provider_options_and_returns_image()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(
                "https://api.clarifai.com/v2/ext/openai/v1/images/generations",
                request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(3, body.RootElement.GetProperty("steps").GetInt32());
            Assert.Equal("b64_json", body.RootElement.GetProperty("response_format").GetString());
            return JsonResponse("""
                {
                  "created": 1716768000,
                  "data": [{"b64_json": "AQID"}],
                  "usage": {"input_tokens": 1, "output_tokens": 2, "total_tokens": 3}
                }
                """);
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "https://clarifai.com/user/app/models/image-model",
            Prompt = "A sunset",
            ProviderOptions = new()
            {
                ["clarifai"] = JsonSerializer.SerializeToElement(new { steps = 3 })
            }
        });

        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
        Assert.Equal(3, response.Usage!.TotalTokens);
    }

    [Fact]
    public async Task EmbeddingRequest_uses_compatible_endpoint_and_preserves_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.clarifai.com/v2/ext/openai/v1/embeddings", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("https://clarifai.com/user/app/models/embed-model", body.RootElement.GetProperty("model").GetString());
            return JsonResponse("""
                {
                  "object": "list",
                  "model": "https://clarifai.com/user/app/models/embed-model",
                  "data": [{"object": "embedding", "index": 0, "embedding": [0.1, 0.2]}],
                  "usage": {"prompt_tokens": 4, "total_tokens": 4}
                }
                """);
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "https://clarifai.com/user/app/models/embed-model",
            Values = ["text"]
        });

        Assert.Equal(4, response.Usage!.Tokens);
        Assert.Equal(new[] { 0.1f, 0.2f }, Assert.Single(response.Embeddings));
    }

    private static ClarifaiProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = "test-key")
        => new(
            new StaticApiKeyResolver(apiKey),
            new StaticHttpClientFactory(responder),
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

    private sealed class StaticHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticResponseHandler(responder));
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
