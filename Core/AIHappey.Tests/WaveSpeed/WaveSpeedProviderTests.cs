using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.WaveSpeed;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.WaveSpeed;

public sealed class WaveSpeedProviderTests
{
    [Fact]
    public async Task ListModels_discovers_live_catalog_and_preserves_native_ids_and_metadata()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return JsonResponse("""
                {"object":"list","data":[
                  {"id":"anthropic/claude-model","owned_by":"anthropic","created":123,
                   "context_window":8192,"supported_parameters":["tools"]}
                ]}
                """);
        });

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("wavespeed/anthropic/claude-model", model.Id);
        Assert.Equal("anthropic/claude-model", model.Name);
        Assert.Equal("anthropic", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal(123, model.Created);
        Assert.Equal(8192, model.ContextWindow);
        Assert.Equal("tools", model.Metadata!["supported_parameters"][0].GetString());
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListModels_allows_public_catalog_discovery_without_a_key()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return JsonResponse("""{"data":[{"id":"provider/model","owned_by":"provider"}]}""");
        }, apiKey: null);

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("wavespeed/provider/model", model.Id);
    }

    [Fact]
    public async Task CompleteChat_uses_openai_compatible_endpoint_and_forwards_native_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("anthropic/claude-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(3, body.RootElement.GetProperty("top_k").GetInt32());

            return JsonResponse("""
                {"id":"completion-1","object":"chat.completion","created":1,
                 "model":"anthropic/claude-model","choices":[],
                 "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
                """);
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "anthropic/claude-model",
            AdditionalProperties = new()
            {
                ["top_k"] = JsonSerializer.SerializeToElement(3)
            }
        });

        var result = JsonSerializer.SerializeToElement(response);
        Assert.Equal(5, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_preserves_native_stream_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "text/event-stream");
            const string events = """
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"model","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"model","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

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
                           Model = "model",
                           Stream = true,
                           StreamOptions = new StreamOptions { IncludeUsage = true }
                       }))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        var usage = JsonSerializer.SerializeToElement(updates[^1]).GetProperty("usage");
        Assert.Equal(3, usage.GetProperty("total_tokens").GetInt32());
    }

    private static WaveSpeedProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = "test-key")
        => new(
            new StaticApiKeyResolver(apiKey),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

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
