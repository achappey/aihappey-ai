using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.WaveSpeed;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
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
    public async Task ListModels_converts_documented_usd_per_million_pricing_and_excludes_tags()
    {
        var provider = CreateProvider(_ => JsonResponse("""
            {"data":[{"id":"provider/model","currency":"USD",
              "pricing":{"unit":"per_1m_tokens","input":3,"output":15,
                "input_cache_read":0.3,"input_cache_write":3},
              "tags":["text","tools"]}]}
            """));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal(0.000003m, model.Pricing!.Input);
        Assert.Equal(0.000015m, model.Pricing.Output);
        Assert.Equal(0.0000003m, model.Pricing.InputCacheRead);
        Assert.Equal(0.000003m, model.Pricing.InputCacheWrite);
        Assert.DoesNotContain("tags", model.Metadata!.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListModels_omits_prices_with_unknown_currency_or_units()
    {
        var provider = CreateProvider(_ => JsonResponse("""
            {"data":[
              {"id":"provider/eur","currency":"EUR","pricing":{"unit":"per_1m_tokens","input":1,"output":2}},
              {"id":"provider/unknown","currency":"USD","pricing":{"unit":"per_request","input":1,"output":2}},
              {"id":"provider/missing","pricing":{"input":1,"output":2}}
            ]}
            """));

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal(3, models.Length);
        Assert.All(models, model => Assert.Null(model.Pricing));
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

    [Fact]
    public async Task ResponsesAsync_uses_native_endpoint_and_preserves_provider_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(3, body.RootElement.GetProperty("top_k").GetInt32());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            return JsonResponse("""
                {"id":"resp_1","object":"response","created_at":1,"model":"model","status":"completed",
                 "output":[],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}
                """);
        });

        var response = await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
            """{"model":"model","input":"hello","metadata":{"wavespeed":{"top_k":3}}}""")!);

        Assert.Equal("wavespeed/model", response.Model);
        Assert.Equal(3, response.NormalizedUsage!.TotalTokens);
    }

    [Fact]
    public async Task ResponsesStreaming_preserves_native_events_and_ignores_keepalive_comments()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            return Sse("""
                : keepalive

                event: response.output_text.delta
                data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"hello","sequence_number":1}

                event: response.completed
                data: {"type":"response.completed","sequence_number":2,"response":{"id":"resp_1","object":"response","created_at":1,"model":"model","status":"completed","output":[],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}}

                event: error
                data: {"type":"error","message":"stream failed","code":"upstream_error"}

                """);
        });

        var parts = new List<ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new ResponseRequest { Model = "model", Stream = true }))
            parts.Add(part);

        Assert.Equal("hello", Assert.IsType<ResponseOutputTextDelta>(parts[0]).Delta);
        var completed = Assert.IsType<ResponseCompleted>(parts[1]);
        Assert.Equal(3, completed.Response.NormalizedUsage!.TotalTokens);
        Assert.Equal("stream failed", Assert.IsType<ResponseError>(parts[2]).Message);
    }

    [Fact]
    public async Task MessagesAsync_uses_native_endpoint_and_preserves_options_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(4, body.RootElement.GetProperty("top_k").GetInt32());
            Assert.Equal("system prompt", body.RootElement.GetProperty("system").GetString());
            Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("weather", body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal("call_1", body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
            return JsonResponse("""
                {"id":"msg_1","type":"message","role":"assistant","model":"model",
                 "content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn",
                 "usage":{"input_tokens":2,"output_tokens":1}}
                """);
        });

        var response = await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
            """
            {"model":"model","max_tokens":10,"system":"system prompt","top_k":4,
             "thinking":{"type":"enabled","budget_tokens":1024},
             "tools":[{"name":"weather","description":"Get weather","input_schema":{"type":"object","properties":{}}}],
             "messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"sunny"}]}]}
            """)!, []);

        Assert.Equal("wavespeed/model", response.Model);
        Assert.Equal(2, response.Usage!.InputTokens);
        Assert.Equal(1, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task MessagesStreaming_preserves_usage_and_streaming_error_events()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            return Sse("""
                : keepalive

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

                event: error
                data: {"type":"error","error":{"type":"overloaded_error","message":"try again"}}

                """);
        });

        var parts = new List<MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new MessagesRequest { Model = "model", Stream = true }, []))
            parts.Add(part);

        Assert.Equal(2, parts[0].Usage!.OutputTokens);
        var error = Assert.IsType<MessageStreamError>(parts[1].Error);
        Assert.Equal("overloaded_error", error.Type);
        Assert.Equal("try again", error.Message);
    }

    [Fact]
    public async Task Native_endpoints_surface_upstream_error_payloads()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"invalid request"}}""")
        });

        var responseError = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.ResponsesAsync(new ResponseRequest { Model = "model" }));
        var messageError = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.MessagesAsync(new MessagesRequest { Model = "model" }, []));

        Assert.Contains("invalid request", responseError.Message);
        Assert.Contains("invalid request", messageError.Message);
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

    private static HttpResponseMessage Sse(string events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
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
