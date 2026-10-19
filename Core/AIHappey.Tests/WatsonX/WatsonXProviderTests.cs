using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.WatsonX;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.WatsonX;

public sealed class WatsonXProviderTests
{
    [Fact]
    public async Task ListModels_exchanges_iam_key_and_preserves_chat_model_metadata()
    {
        var tokenRequests = 0;
        var modelRequests = 0;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://iam.cloud.ibm.com/identity/token")
            {
                tokenRequests++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Contains("grant_type=urn%3Aibm%3Aparams%3Aoauth%3Agrant-type%3Aapikey", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Contains("apikey=test-key", request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"access_token":"iam-token"}""");
            }

            modelRequests++;
            Assert.Equal(
                "https://us-south.ml.cloud.ibm.com/ml/v1/foundation_model_specs?version=2024-05-31&filters=function_text_chat&limit=200",
                request.RequestUri.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("iam-token", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                {
                  "resources": [
                    {
                      "model_id": "ibm/granite-4-h-small",
                      "label": "Granite 4 H Small",
                      "provider": "IBM",
                      "short_description": "A compact Granite model.",
                      "tasks": [{ "id": "text_chat" }]
                    }
                  ],
                  "limit": 200
                }
                """);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Single(models);
        Assert.Equal("watsonx/ibm/granite-4-h-small", models[0].Id);
        Assert.Equal("Granite 4 H Small", models[0].Name);
        Assert.Equal("IBM", models[0].OwnedBy);
        Assert.Equal("A compact Granite model.", models[0].Description);
        Assert.Equal("text_chat", models[0].Metadata!["tasks"][0].GetProperty("id").GetString());
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, tokenRequests);
        Assert.Equal(1, modelRequests);
    }

    [Fact]
    public async Task CompleteChat_forwards_native_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://iam.cloud.ibm.com/identity/token")
                return JsonResponse("""{"access_token":"iam-token"}""");

            Assert.Equal(
                "https://us-south.ml.cloud.ibm.com/ml/v1/text/chat?version=2024-05-31",
                request.RequestUri.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("iam-token", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("ibm/granite-4-h-small", body.RootElement.GetProperty("model_id").GetString());
            Assert.Equal("project-123", body.RootElement.GetProperty("project_id").GetString());
            Assert.Equal("object", body.RootElement.GetProperty("guided_json").GetProperty("type").GetString());

            return JsonResponse("""
                {
                  "id": "chatcmpl-1",
                  "created": 1,
                  "model_id": "ibm/granite-4-h-small",
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
            Model = "ibm/granite-4-h-small",
            AdditionalProperties = new()
            {
                ["project_id"] = JsonSerializer.SerializeToElement("project-123"),
                ["guided_json"] = JsonSerializer.SerializeToElement(new { type = "object" })
            },
        });

        var result = JsonSerializer.SerializeToElement(response);
        Assert.Equal("watsonx/ibm/granite-4-h-small", result.GetProperty("model").GetString());
        Assert.Equal("hello", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(3, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_preserves_streamed_text_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://iam.cloud.ibm.com/identity/token")
                return JsonResponse("""{"access_token":"iam-token"}""");

            Assert.Equal(
                "https://us-south.ml.cloud.ibm.com/ml/v1/text/chat_stream?version=2024-05-31",
                request.RequestUri!.AbsoluteUri);
            const string events = """
                data: {"id":"chat-1","created":1,"model_id":"ibm/granite-4-h-small","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

                data: {"id":"chat-1","created":1,"model_id":"ibm/granite-4-h-small","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

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
                           Model = "ibm/granite-4-h-small",
                           Stream = true
                       }))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        var choice = Assert.IsType<JsonElement>(Assert.Single(updates[0].Choices));
        Assert.Equal("hello", choice.GetProperty("delta").GetProperty("content").GetString());
        var usage = Assert.IsType<JsonElement>(updates[1].Usage);
        Assert.Equal(3, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task ListModels_without_key_or_regional_endpoint_returns_empty()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException(), apiKey: null);
        Assert.Empty(await provider.ListModels());
    }

    private static WatsonXProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = "test-key",
        string? endpoint = "https://us-south.ml.cloud.ibm.com")
        => new(
            new StaticApiKeyResolver(apiKey),
            Options.Create(new WatsonXProviderOptions { Endpoint = endpoint }),
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
