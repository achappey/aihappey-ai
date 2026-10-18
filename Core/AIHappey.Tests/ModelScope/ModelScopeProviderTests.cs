using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.ModelScope;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ModelScope;

public sealed class ModelScopeProviderTests
{
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
                    { "id": "Tongyi-MAI/Z-Image-Turbo", "task": "text-to-image" },
                    { "id": "zai-org/GLM-4.5" },
                    { "object": "model" }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal(3, models.Length);
        Assert.Equal("modelscope/Qwen/Qwen3-1.7B:DashScope", models[0].Id);
        Assert.Equal("Qwen/Qwen3-1.7B:DashScope", models[0].Name);
        Assert.Equal("Qwen", models[0].OwnedBy);
        Assert.Equal(1686935002, models[0].Created);
        Assert.Equal("language", models[0].Type);
        Assert.True(models[0].Metadata!["custom_metadata"].GetProperty("supports_reasoning").GetBoolean());
        Assert.Equal("modelscope/Tongyi-MAI/Z-Image-Turbo", models[1].Id);
        Assert.Equal("image", models[1].Type);
        Assert.Equal("modelscope/zai-org/GLM-4.5", models[2].Id);
        Assert.Equal("modelscope", models[2].OwnedBy);
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

    [Fact]
    public async Task ImageRequest_submits_native_options_polls_and_downloads_images_without_forwarding_credentials()
    {
        var requests = new List<HttpRequestMessage>();
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse("""{"task_id":"task/123","task_status":"SUCCEED"}"""),
            JsonResponse("""{"task_status":"RUNNING"}"""),
            JsonResponse("""{"task_status":"SUCCEED","output_images":["https://images.example/output.png"]}"""),
            ImageResponse([1, 2, 3])
        ]);
        var provider = CreateProvider(request =>
        {
            requests.Add(request);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
                Assert.Equal("true", Assert.Single(request.Headers.GetValues("X-ModelScope-Async-Mode")));
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("Tongyi-MAI/Z-Image-Turbo", body.RootElement.GetProperty("model").GetString());
                Assert.Equal("a small red bird", body.RootElement.GetProperty("prompt").GetString());
                Assert.Equal("1024x768", body.RootElement.GetProperty("size").GetString());
                Assert.Equal(42, body.RootElement.GetProperty("seed").GetInt32());
                Assert.Equal(8, body.RootElement.GetProperty("steps").GetInt32());
            }
            else if (request.RequestUri!.AbsoluteUri.Contains("/v1/tasks/", StringComparison.Ordinal))
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("image_generation", Assert.Single(request.Headers.GetValues("X-ModelScope-Task-Type")));
                Assert.Equal("https://api-inference.modelscope.ai/v1/tasks/task%2F123", request.RequestUri.AbsoluteUri);
            }
            else
            {
                Assert.Equal("https://images.example/output.png", request.RequestUri!.AbsoluteUri);
                Assert.Null(request.Headers.Authorization);
            }

            return responses.Dequeue();
        });

        var result = await provider.ImageRequest(new ImageRequest
        {
            Model = "modelscope/Tongyi-MAI/Z-Image-Turbo",
            Prompt = "a small red bird",
            Size = "1024x768",
            Seed = 42,
            ProviderOptions = new()
            {
                ["modelscope"] = JsonSerializer.SerializeToElement(new { steps = 8 })
            }
        });

        Assert.Equal("modelscope/Tongyi-MAI/Z-Image-Turbo", result.Response.ModelId);
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(result.Images!));
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public async Task ImageRequest_maps_async_task_failure_to_error()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse("""{"task_id":"task-1"}"""),
            JsonResponse("""{"task_status":"FAILED","errors":{"message":"model unavailable"}}""")
        ]);
        var provider = CreateProvider(_ => responses.Dequeue());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "Tongyi-MAI/Z-Image-Turbo",
            Prompt = "a small red bird"
        }));

        Assert.Contains("model unavailable", error.Message);
    }

    [Fact]
    public async Task OpenAIImageGeneration_maps_modelscope_image_to_base64_response()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse("""{"task_id":"task-1"}"""),
            JsonResponse("""{"task_status":"SUCCEED","output_images":["https://images.example/output.png"]}"""),
            ImageResponse([1, 2, 3])
        ]);
        var provider = CreateProvider(_ => responses.Dequeue());

        var result = await provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = "modelscope/Tongyi-MAI/Z-Image-Turbo",
            Prompt = "a small red bird"
        });

        Assert.Equal("AQID", Assert.Single(result.Data!).B64Json);
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

    private static HttpResponseMessage ImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new("image/png");
        return response;
    }

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
