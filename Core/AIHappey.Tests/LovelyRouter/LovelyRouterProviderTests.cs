using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Providers.LovelyRouter;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.LovelyRouter;

public sealed class LovelyRouterProviderTests
{
    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"gemini-test","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;

    private const string ChatEvents = """
        : keepalive

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gemini-test","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gemini-test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gemini-test","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

        data: [DONE]

        """;

    [Fact]
    public void Registration_resolves_scoped_provider_and_header_key()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-LovelyRouter-Key"] = "router-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("router-key", resolver.Resolve("lovelyrouter"));

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddProviders();
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();
        var registry = container.GetRequiredService<ProviderRegistry>();
        Assert.Contains("lovelyrouter", registry.ModelProviderIds);
        var provider = registry.GetModelProvider(scope.ServiceProvider, "lovelyrouter");
        Assert.IsType<LovelyRouterProvider>(provider);
        Assert.Same(provider, scope.ServiceProvider.GetRequiredService<LovelyRouterProvider>());
    }

    [Fact]
    public async Task Discovery_is_public_cached_and_preserves_native_metadata_and_prices()
    {
        var calls = 0;
        var provider = Create(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://lovelyrouter.com/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Json("""
                {"object":"list","has_more":false,"data":[
                  {"id":"vendor/native-model","type":"model","display_name":"Native Model","owned_by":"Google","created":1700000000,"context_length":1048576,"pricing":{"input":2,"output":10,"cache_read":0.1,"cache_write":null,"unit":"USD per 1M tokens"},"groups":["gemini-value"],"extra":"preserved"}
                ]}
                """);
        }, key: null);

        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("lovelyrouter/vendor/native-model", model.Id);
        Assert.Equal("Native Model", model.Name);
        Assert.Equal("Google", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal("language", JsonSerializer.SerializeToElement(model).GetProperty("type").GetString());
        Assert.Equal(1700000000, model.Created);
        Assert.Equal(1048576, model.ContextWindow);
        Assert.Equal(0.000002m, model.Pricing!.Input);
        Assert.Equal(0.000010m, model.Pricing.Output);
        Assert.Equal(0.0000001m, model.Pricing.InputCacheRead);
        Assert.Equal(model.Pricing.Input, model.Pricing.InputCacheWrite);
        Assert.Equal("gemini-value", model.Metadata!["groups"][0].GetString());
        Assert.Equal("preserved", model.Metadata["extra"].GetString());
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Discovery_does_not_hide_http_or_schema_errors()
    {
        var provider = Create(_ => Json("""{"error":{"message":"unavailable"}}""", HttpStatusCode.ServiceUnavailable));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Contains("unavailable", error.Message);

        provider = Create(_ => Json("""{"data":[{"id":""}]}"""));
        await Assert.ThrowsAsync<JsonException>(() => provider.ListModels());
    }

    [Fact]
    public async Task Discovery_propagates_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Create(_ => throw new InvalidOperationException("Must not send a cancelled request."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancellation.Token));
    }

    [Theory]
    [InlineData("x-lovely-group")]
    [InlineData("X-Session-Id")]
    [InlineData("anthropic-version")]
    [InlineData("Anthropic-Beta")]
    [InlineData("openai-beta")]
    public void Routing_and_vendor_headers_are_allowed_without_exposing_credentials(string name)
    {
        Assert.True(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader(name, "lovelyrouter"));
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("X-LovelyRouter-Key", "lovelyrouter"));
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("Authorization", "lovelyrouter"));
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("x-api-key", "lovelyrouter"));
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("x-session-id", "unrelated"));
    }

    [Fact]
    public async Task Chat_preserves_images_tools_structured_output_native_options_and_usage()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("gpt-value", request.Headers.GetValues("x-lovely-group").Single());
            Assert.Equal("conversation-1", request.Headers.GetValues("x-session-id").Single());
            var body = Body(request);
            Assert.Equal("vendor/native-model", body.GetProperty("model").GetString());
            Assert.Equal("image_url", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            Assert.Equal("cache-1", body.GetProperty("prompt_cache_key").GetString());
            return Json(Completion);
        });
        var options = Deserialize<ChatCompletionOptions>("""
            {"model":"lovelyrouter/vendor/native-model","messages":[{"role":"user","content":[{"type":"text","text":"Describe"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]}],"tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object","properties":{}}}}],"response_format":{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object","properties":{}}}},"reasoning_effort":"high","prompt_cache_key":"cache-1"}
            """);
        options.Headers = new() { ["x-lovely-group"] = "gpt-value", ["x-session-id"] = "conversation-1" };

        var result = JsonSerializer.SerializeToElement(await provider.CompleteChatAsync(options));
        Assert.Equal("lovelyrouter/gemini-test", result.GetProperty("model").GetString());
        Assert.Equal(5, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Chat_metadata_passthrough_preserves_advanced_options_and_tool_call_responses()
    {
        var provider = Create(request =>
        {
            Assert.Equal("low", Body(request).GetProperty("reasoning_effort").GetString());
            return Json("""
                {"id":"chat-1","object":"chat.completion","created":1,"model":"gpt-test","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
                """);
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "gpt-test",
            Metadata = new() { ["lovelyrouter"] = JsonSerializer.SerializeToElement(new { reasoning_effort = "low" }) }
        });
        var result = JsonSerializer.SerializeToElement(response);
        Assert.Equal("lookup", result.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Chat_stream_preserves_text_tool_deltas_and_final_usage()
    {
        var provider = Create(request =>
        {
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            return Sse("""
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-test","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":null}]}

                """ + "\n" + ChatEvents);
        });

        var updates = await Collect(provider.CompleteChatStreamingAsync(new() { Model = "lovelyrouter/gpt-test" }));
        Assert.Equal(4, updates.Count);
        Assert.Equal("lovelyrouter/gpt-test", updates[0].Model);
        Assert.Equal("hello", JsonSerializer.SerializeToElement(updates[1]).GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(5, JsonSerializer.SerializeToElement(updates[^1]).GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Messages_preserves_native_anthropic_body_and_headers()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            var body = Body(request);
            Assert.Equal("claude-test", body.GetProperty("model").GetString());
            Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", body.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal("ephemeral", body.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
            return Json("""
                {"id":"msg-1","type":"message","role":"assistant","model":"claude-test","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":3,"cache_read_input_tokens":7}}
                """);
        });
        var response = await provider.MessagesAsync(Deserialize<MessagesRequest>("""
            {"model":"lovelyrouter/claude-test","max_tokens":1024,"system":[{"type":"text","text":"Be helpful","cache_control":{"type":"ephemeral"}}],"messages":[{"role":"user","content":"Hello"}],"thinking":{"type":"enabled","budget_tokens":512},"output_config":{"effort":"high"}}
            """), new() { ["anthropic-version"] = "2023-06-01" });
        var result = JsonSerializer.SerializeToElement(response);
        Assert.Equal("lovelyrouter/claude-test", response.Model);
        Assert.Equal(7, result.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32());
    }

    [Fact]
    public async Task Messages_stream_uses_native_anthropic_events()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            return Sse("""
                event: message_start
                data: {"type":"message_start","message":{"id":"msg-1","type":"message","role":"assistant","model":"claude-test","content":[],"usage":{"input_tokens":2,"output_tokens":0}}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

                event: message_stop
                data: {"type":"message_stop"}

                """);
        });

        var parts = await Collect(provider.MessagesStreamingAsync(Deserialize<MessagesRequest>("""
            {"model":"claude-test","messages":[],"max_tokens":16,"stream":true}
            """), []));
        Assert.Equal(3, parts.Count);
        Assert.Equal("lovelyrouter/claude-test", parts[0].Message!.Model);
        Assert.Equal("message_stop", parts[^1].Type);
    }

    [Fact]
    public async Task Responses_preserves_native_gpt_options_and_disables_storage()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            var body = Body(request);
            Assert.Equal("gpt-test", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.Equal("Hello", body.GetProperty("input").GetString());
            Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("native-value", body.GetProperty("native_option").GetString());
            return Json("""
                {"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[{"id":"msg-1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello","annotations":[]}]}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}
                """);
        });
        var response = await provider.ResponsesAsync(Deserialize<ResponseRequest>("""
            {"model":"lovelyrouter/gpt-test","input":"Hello","store":true,"reasoning":{"effort":"high"},"native_option":"native-value"}
            """));
        Assert.Equal("lovelyrouter/gpt-test", response.Model);
    }

    [Fact]
    public async Task Responses_stream_preserves_native_gpt_events()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.False(Body(request).GetProperty("store").GetBoolean());
            return Sse("""
                event: response.output_text.delta
                data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"content_index":0,"delta":"hello"}

                event: response.completed
                data: {"type":"response.completed","response":{"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"gpt-test","output":[],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}

                """);
        });
        var parts = await Collect(provider.ResponsesStreamingAsync(Deserialize<ResponseRequest>("""
            {"model":"gpt-test","input":"Hello","stream":true}
            """)));
        Assert.Equal(2, parts.Count);
        Assert.Contains("response.completed", JsonSerializer.Serialize(parts[^1]));
    }

    [Fact]
    public async Task Other_models_are_usable_through_messages_and_responses_via_shared_mapping()
    {
        var calls = 0;
        var provider = Create(request =>
        {
            calls++;
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("gemini-test", Body(request).GetProperty("model").GetString());
            return Json(Completion);
        });
        var messages = await provider.MessagesAsync(Deserialize<MessagesRequest>("""
            {"model":"lovelyrouter/gemini-test","messages":[{"role":"user","content":"Hello"}],"max_tokens":16}
            """), []);
        var response = await provider.ResponsesAsync(Deserialize<ResponseRequest>("""
            {"model":"lovelyrouter/gemini-test","input":"Hello"}
            """));
        Assert.Contains("hello", JsonSerializer.Serialize(messages));
        Assert.Contains("hello", JsonSerializer.Serialize(response));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Other_models_stream_through_messages_responses_and_api_chat()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            return Sse(ChatEvents);
        });
        var messages = await Collect(provider.MessagesStreamingAsync(Deserialize<MessagesRequest>("""
            {"model":"gemini-test","messages":[{"role":"user","content":"Hello"}],"max_tokens":16,"stream":true}
            """), []));
        var responses = await Collect(provider.ResponsesStreamingAsync(Deserialize<ResponseRequest>("""
            {"model":"gemini-test","input":"Hello","stream":true}
            """)));
        var chat = await Collect(provider.StreamAsync(Deserialize<ChatRequest>("""
            {"model":"lovelyrouter/gemini-test","messages":[{"id":"user-1","role":"user","parts":[{"type":"text","text":"Hello"}]}],"providerMetadata":{"lovelyrouter":{"reasoning_effort":"low"}}}
            """)));
        Assert.Contains("hello", JsonSerializer.Serialize(messages));
        Assert.Contains("hello", JsonSerializer.Serialize(responses));
        Assert.Contains("hello", JsonSerializer.Serialize(chat));
    }

    [Fact]
    public async Task Api_chat_forwards_provider_options_and_routing_headers()
    {
        var provider = Create(request =>
        {
            var body = Body(request);
            Assert.Equal("gemini-test", body.GetProperty("model").GetString());
            Assert.Equal("cache-1", body.GetProperty("prompt_cache_key").GetString());
            Assert.Equal("gemini-value", request.Headers.GetValues("x-lovely-group").Single());
            Assert.Equal("conversation-1", request.Headers.GetValues("x-session-id").Single());
            return Sse(ChatEvents);
        });
        var chat = Deserialize<ChatRequest>("""
            {"model":"lovelyrouter/gemini-test","messages":[{"id":"user-1","role":"user","parts":[{"type":"text","text":"Hello"}]}],"providerMetadata":{"lovelyrouter":{"prompt_cache_key":"cache-1"}}}
            """);
        chat.Headers = new Dictionary<string, string?>
        {
            ["x-lovely-group"] = "gemini-value",
            ["x-session-id"] = "conversation-1",
            ["X-LovelyRouter-Key"] = "must-not-be-forwarded"
        }.GetProviderPassthroughHeaders("lovelyrouter");

        Assert.NotEmpty(await Collect(provider.StreamAsync(chat)));
    }

    [Fact]
    public async Task Inference_propagates_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Create(_ => throw new InvalidOperationException("Must not send a cancelled request."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CompleteChatAsync(new() { Model = "gpt-test" }, cancellation.Token));
    }

    [Fact]
    public async Task Missing_key_and_native_errors_are_reported()
    {
        var provider = Create(_ => throw new InvalidOperationException("Must not call without a key."), key: null);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new() { Model = "gpt-test" }));
        Assert.Contains("API key", missing.Message);
        provider = Create(_ => Json("""{"error":{"type":"rate_limit_exceeded","message":"capacity busy"}}""", HttpStatusCode.TooManyRequests));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new() { Model = "gpt-test" }));
        Assert.Contains("capacity busy", error.Message);
    }

    private static LovelyRouterProvider Create(Func<HttpRequestMessage, HttpResponseMessage> responder, string? key = "test-key")
        => new(new KeyResolver(key), new ClientFactory(new Handler(responder)),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;
    private static JsonElement Body(HttpRequestMessage request)
        => JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        List<T> items = [];
        await foreach (var item in stream)
            items.Add(item);
        return items;
    }

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "lovelyrouter" ? key : null;
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
