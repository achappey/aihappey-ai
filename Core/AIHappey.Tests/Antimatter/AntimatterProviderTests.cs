using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Antimatter;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Antimatter;

public sealed class AntimatterProviderTests
{
    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"vendor/model","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;

    [Fact]
    public void Registration_resolves_scoped_provider_and_api_key_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Antimatter-Key"] = "antimatter-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("antimatter-key", resolver.Resolve("antimatter"));

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddProviders();
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();
        var registry = container.GetRequiredService<ProviderRegistry>();
        Assert.Contains("antimatter", registry.ModelProviderIds);
        Assert.IsType<AntimatterProvider>(registry.GetModelProvider(scope.ServiceProvider, "antimatter"));
    }

    [Fact]
    public async Task Discovery_requires_key_caches_results_and_maps_catalog_fields()
    {
        var calls = 0;
        var provider = Create(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://useantimatter.com/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Json("""
                {"object":"list","data":[
                  {"id":"vendor/native-model","object":"model","owned_by":"Vendor","name":"Native Model","created":1700000000,"context_length":65536,"max_output":8192,"pricing":{"input":1.25,"output":5},"input_modalities":["text","image"],"route":"balanced"}
                ]}
                """);
        });

        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("antimatter/vendor/native-model", model.Id);
        Assert.Equal("Native Model", model.Name);
        Assert.Equal("Vendor", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal(1700000000, model.Created);
        Assert.Equal(65536, model.ContextWindow);
        Assert.Equal(8192, model.MaxTokens);
        Assert.Equal(0.00000125m, model.Pricing!.Input);
        Assert.Equal(0.000005m, model.Pricing.Output);
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, calls);

        var missingKey = Create(_ => throw new InvalidOperationException("Must not request discovery without a key."), key: null);
        Assert.Empty(await missingKey.ListModels());
    }

    [Fact]
    public async Task Chat_sends_native_model_tools_and_provider_metadata()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            var body = Body(request);
            Assert.Equal("vendor/native-model", body.GetProperty("model").GetString());
            Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            return Json(Completion);
        });
        var options = Deserialize<ChatCompletionOptions>("""
            {"model":"antimatter/vendor/native-model","messages":[{"role":"user","content":"hello"}],"tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}],"providerMetadata":{"antimatter":{"reasoning_effort":"high"}}}
            """);

        var response = await provider.CompleteChatAsync(options);
        Assert.Equal("antimatter/vendor/model", response.Model);
    }

    [Fact]
    public async Task Messages_and_responses_use_native_wires()
    {
        var requests = new List<string>();
        var provider = Create(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1/messages")
            {
                Assert.Equal("vendor/native-model", Body(request).GetProperty("model").GetString());
                Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
                Assert.Equal("comparable", request.Headers.GetValues("x-antimatter-fallback").Single());
                return Json("""
                    {"id":"msg-1","type":"message","role":"assistant","model":"vendor/native-model","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":3}}
                    """);
            }

            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            Assert.Equal("vendor/native-model", Body(request).GetProperty("model").GetString());
            Assert.Equal("native-value", Body(request).GetProperty("native_option").GetString());
            Assert.Equal("required", request.Headers.GetValues("x-antimatter-private").Single());
            return Json("""
                {"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"vendor/native-model","output":[{"id":"msg-1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello","annotations":[]}]}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}
                """);
        });

        var message = await provider.MessagesAsync(Deserialize<MessagesRequest>("""
            {"model":"antimatter/vendor/native-model","max_tokens":100,"messages":[{"role":"user","content":"hello"}]}
            """), new() { ["anthropic-version"] = "2023-06-01", ["x-antimatter-fallback"] = "comparable" });
        var responseRequest = Deserialize<ResponseRequest>("""
            {"model":"antimatter/vendor/native-model","input":"hello","native_option":"native-value"}
            """);
        responseRequest.Headers = new() { ["x-antimatter-private"] = "required" };
        var response = await provider.ResponsesAsync(responseRequest);

        Assert.Equal("antimatter/vendor/native-model", message.Model);
        Assert.Equal("antimatter/vendor/native-model", response.Model);
        Assert.Equal(["/v1/messages", "/v1/responses"], requests);
    }

    [Fact]
    public async Task Chat_stream_and_api_chat_forward_metadata()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            Assert.Equal("low", Body(request).GetProperty("reasoning_effort").GetString());
            return Sse("""
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}

                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """);
        });

        var chat = Deserialize<ChatRequest>("""
            {"model":"antimatter/vendor/model","messages":[{"id":"user-1","role":"user","parts":[{"type":"text","text":"hello"}]}],"providerMetadata":{"antimatter":{"reasoning_effort":"low"}}}
            """);
        var updates = await Collect(provider.StreamAsync(chat));
        Assert.NotEmpty(updates);
        Assert.Contains("hello", JsonSerializer.Serialize(updates));
    }

    [Fact]
    public async Task Native_chat_messages_and_responses_streams_use_their_documented_wires()
    {
        var provider = Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.True(Body(request).GetProperty("stream").GetBoolean());
            return path switch
            {
                "/v1/chat/completions" => Sse("""
                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}

                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]

                    """),
                "/v1/messages" => Sse("""
                    event: message_start
                    data: {"type":"message_start","message":{"id":"msg-1","type":"message","role":"assistant","model":"vendor/model","content":[],"usage":{"input_tokens":2,"output_tokens":0}}}

                    event: content_block_delta
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

                    event: message_stop
                    data: {"type":"message_stop"}

                    """),
                "/v1/responses" => Sse("""
                    event: response.output_text.delta
                    data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"content_index":0,"delta":"hello"}

                    event: response.completed
                    data: {"type":"response.completed","response":{"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"vendor/model","output":[],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}

                    """),
                _ => throw new InvalidOperationException($"Unexpected Antimatter stream route: {path}")
            };
        });

        var chat = await Collect(provider.CompleteChatStreamingAsync(new() { Model = "antimatter/vendor/model" }));
        var messages = await Collect(provider.MessagesStreamingAsync(Deserialize<MessagesRequest>("""
            {"model":"antimatter/vendor/model","messages":[{"role":"user","content":"hello"}],"max_tokens":32,"stream":true}
            """), []));
        var responses = await Collect(provider.ResponsesStreamingAsync(Deserialize<ResponseRequest>("""
            {"model":"antimatter/vendor/model","input":"hello","stream":true}
            """)));

        Assert.NotEmpty(chat);
        Assert.NotEmpty(messages);
        Assert.NotEmpty(responses);
    }

    [Fact]
    public async Task Discovery_and_inference_report_http_errors()
    {
        var provider = Create(_ => Json("""{"error":{"message":"capacity busy"}}""", HttpStatusCode.ServiceUnavailable));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Contains("capacity busy", error.Message);

        provider = Create(_ => Json("""{"error":{"message":"capacity busy"}}""", HttpStatusCode.TooManyRequests));
        error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CompleteChatAsync(new() { Model = "vendor/model" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    private static AntimatterProvider Create(Func<HttpRequestMessage, HttpResponseMessage> responder, string? key = "test-key")
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
        public string? Resolve(string provider) => provider == "antimatter" ? key : null;
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
