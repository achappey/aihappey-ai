using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Plamo;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Plamo;

public sealed class PlamoProviderTests
{
    private const string Catalog = """
        {"object":"list","data":[
          {"id":"plamo-3.0-prime","object":"model","created":1698766473,"owned_by":"preferredai","native":{"value":7}},
          {"id":"plamo-3.0-prime","object":"model"},
          {"id":"tenant/custom-chat","owned_by":"preferredai"},
          {"id":""},{"id":" "},{"id":null},{},null,42
        ]}
        """;

    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"plamo-3.0-prime",
         "choices":[{"index":0,"message":{"role":"assistant","content":"こんにちは"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;

    private const string Stream = """
        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"plamo-3.0-prime","choices":[{"index":0,"delta":{"content":"こんにちは"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"plamo-3.0-prime","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"plamo-3.0-prime","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

        data: [DONE]

        """;

    [Fact]
    public async Task DiscoveryPreservesNativeIdentifiersAndScopesCacheToCredentials()
    {
        var calls = 0;
        var keys = new Keys("first-key");
        var provider = Create((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.platform.preferredai.jp/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(keys.Key, request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(Catalog));
        }, keys);

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["plamo/plamo-3.0-prime", "plamo/tenant/custom-chat"], models.Select(model => model.Id));
        Assert.All(models, model => Assert.Equal("language", model.Type));
        Assert.Equal("plamo-3.0-prime", models[0].Name);
        Assert.Equal("preferredai", models[0].OwnedBy);
        Assert.Equal(1698766473, models[0].Created);
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(1, calls);
        keys.Key = "second-key";
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeySkipsDiscoveryAndRejectsChat(string? key)
    {
        var provider = Create(NoNetwork, new(key));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(Options()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(Options())) { }
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{")]
    public async Task InvalidDiscoveryFailsWithoutInventedFallback(string body)
        => await Assert.ThrowsAnyAsync<JsonException>(() => Create((_, _) =>
            Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task EmptyDiscoveryStaysEmpty()
        => Assert.Empty(await Create((_, _) =>
            Task.FromResult(Json("""{"data":[]}"""))).ListModels());

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DiscoveryPreservesErrorsAndDoesNotCacheFailures(HttpStatusCode status)
    {
        var calls = 0;
        var provider = Create((_, _) => Task.FromResult(++calls == 1
            ? Json("""{"error":{"message":"native detail"}}""", status)
            : Json(Catalog)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("native detail", error.Message);
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DiscoveredModelIsUsableAndNativeParametersPassThroughUnchanged()
    {
        var options = Options();
        options.AdditionalProperties = new()
        {
            ["top_k"] = Element(7),
            ["repetition_penalty"] = Element(1.1),
            ["future_option"] = Element(new { nested = new object[] { true, "native", 42 } })
        };
        var original = JsonSerializer.Serialize(options.AdditionalProperties);
        var provider = Create(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(Catalog);
            AssertChat(request);
            var body = await Body(request, token);
            Assert.Equal("plamo-3.0-prime", body.GetProperty("model").GetString());
            Assert.Equal("こんにちは", body.GetProperty("messages")[0].GetProperty("content").GetString());
            foreach (var (name, value) in options.AdditionalProperties)
                Assert.Equal(value.GetRawText(), body.GetProperty(name).GetRawText());
            return Json(Completion);
        });
        options.Model = (await provider.ListModels()).First().Id.SplitModelId().Model;
        var result = Element(await provider.CompleteChatAsync(options));
        Assert.Equal("plamo/plamo-3.0-prime", result.GetProperty("model").GetString());
        Assert.Equal("こんにちは", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        AssertUsage(result.GetProperty("usage"));
        Assert.Equal(original, JsonSerializer.Serialize(options.AdditionalProperties));
    }

    [Fact]
    public async Task StreamingPassesNativeOptionsAndPreservesReturnedUsage()
    {
        var provider = Create(async (request, token) =>
        {
            AssertChat(request);
            var body = await Body(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal(7, body.GetProperty("top_k").GetInt32());
            return Sse();
        });
        var options = Options();
        options.Stream = true;
        options.AdditionalProperties = new() { ["top_k"] = Element(7) };
        var updates = new List<JsonElement>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            updates.Add(Element(update));
        Assert.Equal("こんにちは", updates[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        AssertUsage(updates.Single(update => update.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object).GetProperty("usage"));
    }

    [Fact]
    public async Task StandardAdaptersUseChatAndForwardProviderOptions()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertChat(request);
            var body = await Body(request, token);
            if (body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
                return Sse();
            if (calls == 1)
                Assert.Equal(9, body.GetProperty("top_k").GetInt32());
            return Json(Completion);
        });
        var unified = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "plamo", Model = "plamo-3.0-prime",
            Metadata = new() { ["plamo"] = Element(new { top_k = 9 }) }
        });
        Assert.Equal(5, unified.NormalizedUsage!.TotalTokens);
        var response = await provider.ResponsesAsync(new ResponseRequest { Model = "plamo-3.0-prime", Input = "Hi" });
        Assert.Equal("completed", response.Status);
        var message = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "plamo-3.0-prime", MaxTokens = 20, Messages = []
        }, []);
        Assert.NotNull(message);

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(new() { ProviderId = "plamo", Model = "plamo-3.0-prime" }))
            events.Add(item);
        Assert.Contains(events, item => item.Event.Type == "text-delta");
        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(events, item => item.Event.Type == "finish").Event.Data);
        Assert.Equal(2, finish.InputTokens);
        Assert.Equal(3, finish.OutputTokens);
        Assert.Equal(5, finish.TotalTokens);
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatErrorsPreserveNativeMessage(bool streaming)
    {
        var provider = Create((_, _) => Task.FromResult(Json(
            """{"error":{"message":"native chat detail"}}""", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (streaming)
                await foreach (var _ in provider.CompleteChatStreamingAsync(Options())) { }
            else
                await provider.CompleteChatAsync(Options());
        });
        Assert.Contains("native chat detail", error.Message);
    }

    [Fact]
    public async Task VercelStreamingForwardsNativeOptionsAndOtherStreamingAdaptersUseChat()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertChat(request);
            var body = await Body(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            if (calls == 1)
                Assert.Equal(11, body.GetProperty("top_k").GetInt32());
            return Sse();
        });
        var chat = JsonSerializer.Deserialize<ChatRequest>("""
            {"id":"chat-1","model":"plamo/plamo-3.0-prime",
             "messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}],
             "providerMetadata":{"plamo":{"top_k":11}}}
            """, JsonSerializerOptions.Web)!;
        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(chat))
            parts.Add(part);
        Assert.Equal("こんにちは", string.Concat(parts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta)));

        var messageParts = new List<MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new MessagesRequest
        {
            Model = "plamo-3.0-prime", MaxTokens = 20, Messages = []
        }, []))
            messageParts.Add(part);
        Assert.NotEmpty(messageParts);

        var responseParts = new List<AIHappey.Responses.Streaming.ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new ResponseRequest
        {
            Model = "plamo-3.0-prime", Input = "Hi"
        }))
            responseParts.Add(part);
        Assert.NotEmpty(responseParts);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("discovery", false)]
    [InlineData("chat", false)]
    [InlineData("stream", false)]
    [InlineData("discovery", true)]
    [InlineData("chat", true)]
    [InlineData("stream", true)]
    public async Task CancellationPropagates(string operation, bool inflight)
    {
        using var cancellation = new CancellationTokenSource();
        if (!inflight)
            cancellation.Cancel();
        var provider = Create(async (_, token) =>
        {
            if (inflight)
                cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "discovery": await provider.ListModels(cancellation.Token); break;
                case "chat": await provider.CompleteChatAsync(Options(), cancellation.Token); break;
                default:
                    await foreach (var _ in provider.CompleteChatStreamingAsync(Options(), cancellation.Token)) { }
                    break;
            }
        });
    }

    [Fact]
    public void HeaderAndScopedProviderRegistrationAreAvailable()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Plamo-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("X-Plamo-Key", HeaderApiKeyResolver.SupportedProviderHeaders["plamo"]);
        Assert.Equal("header-key", resolver.Resolve("PLAMO"));
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            descriptor => descriptor.ImplementationType == typeof(PlamoProvider)).Lifetime);
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("plamo"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<PlamoProvider>(),
            registry.GetModelProvider(scope.ServiceProvider, "plamo"));
    }

    private static ChatCompletionOptions Options() => new()
    {
        Model = "plamo-3.0-prime", Messages = [new() { Role = "user", Content = Element("こんにちは") }]
    };

    private static PlamoProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, Keys? keys = null)
        => new(keys ?? new("test-key"), new Factory(responder),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse()
        => new(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
    private static void AssertChat(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.platform.preferredai.jp/v1/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
    }
    private static void AssertUsage(JsonElement usage)
    {
        Assert.Equal(2, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(3, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("total_tokens").GetInt32());
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider) => provider == "plamo" ? Key : null;
    }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
