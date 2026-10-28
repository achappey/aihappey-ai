using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.GWDG;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.GWDG;

public sealed class GWDGProviderTests
{
    private const string Models = """
        {"object":"list","data":[
          {"id":"vendor/chat","name":"Native Chat","owned_by":"GWDG","created":123,
           "input":["text","image"],"output":["text"],"contextLength":32768,
           "external":false,"status":"available","future":{"enabled":true}},
          {"id":"vendor/chat","name":"Duplicate","input":["text"],"output":["text"]},
          {"id":"vision","input":["image","text"],"output":["image","text"]},
          {"id":"embedding","input":["text"],"output":["embedding"]},
          {"id":"speech","input":["text"],"output":["audio"]},
          {"id":"transcription","input":["audio"],"output":["text"]},
          {"id":"image","input":["text"],"output":["image"]},
          {"id":"missing-modalities"},
          {"id":"missing-input","output":["text"]},
          {"id":"missing-output","input":["text"]},
          {"id":"empty-input","input":[],"output":["text"]},
          {"id":"empty-output","input":["text"],"output":[]},
          {"id":"","input":["text"],"output":["text"]},
          {"id":"  ","input":["text"],"output":["text"]}
        ]}
        """;

    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"vendor/chat",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;

    private const string Stream = """
        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/chat","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/chat","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

        data: [DONE]

        """;

    [Fact]
    public async Task DiscoveryUsesAuthenticatedNativeCatalogAndMapsTextModelFields()
    {
        var provider = CreateProvider((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://chat-ai.academiccloud.de/v1/models", request.RequestUri!.AbsoluteUri);
            AssertAuth(request);
            return Task.FromResult(JsonResponse(Models));
        });

        Assert.Equal("gwdg", provider.GetIdentifier());
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["gwdg/vendor/chat", "gwdg/vision"], models.Select(model => model.Id));
        Assert.All(models, model => Assert.Equal("language", model.Type));
        var chat = models[0];
        Assert.Equal("Native Chat", chat.Name);
        Assert.Equal("GWDG", chat.OwnedBy);
        Assert.Equal(123, chat.Created);
    }

    [Fact]
    public async Task DiscoveryCachesCatalogWithoutLeakingAcrossCredentials()
    {
        var calls = 0;
        var key = new KeyResolver("first-key");
        var provider = CreateProvider((request, _) =>
        {
            calls++;
            Assert.Equal(key.Key, request.Headers.Authorization!.Parameter);
            return Task.FromResult(JsonResponse(Models));
        }, key);

        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(1, calls);
        key.Key = "second-key";
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
        key.Key = null;
        Assert.Empty(await provider.ListModels());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeySkipsDiscoveryAndRejectsChat(string? key)
    {
        var provider = CreateProvider(NoNetwork, new(key));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(ChatOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions())) { }
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{")]
    public async Task MalformedCatalogThrowsJsonExceptionWithoutFallback(string json)
        => await Assert.ThrowsAnyAsync<JsonException>(() =>
            CreateProvider((_, _) => Task.FromResult(JsonResponse(json))).ListModels());

    [Fact]
    public async Task EmptyCatalogRemainsEmpty()
        => Assert.Empty(await CreateProvider((_, _) =>
            Task.FromResult(JsonResponse("""{"data":[]}"""))).ListModels());

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DiscoveryPreservesErrorStatusAndNativeMessageAndDoesNotCacheFailures(HttpStatusCode status)
    {
        var calls = 0;
        var provider = CreateProvider((_, _) => Task.FromResult(++calls == 1
            ? JsonResponse("""{"error":{"message":"native discovery detail"}}""", status)
            : JsonResponse(Models)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("native discovery detail", error.Message);
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NativeChatForwardsOptionsUnchangedAndPreservesUsage()
    {
        var options = ChatOptions();
        options.AdditionalProperties = new()
        {
            ["chat_template_kwargs"] = Element(new { enable_thinking = false, nested = new { value = 7 } }),
            ["reasoning_effort"] = Element("high"),
            ["future_option"] = Element(new { values = new object[] { true, "native", 42 } })
        };
        var original = JsonSerializer.Serialize(options.AdditionalProperties);
        var provider = CreateProvider(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://chat-ai.academiccloud.de/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            AssertAuth(request);
            var body = await ReadBody(request, token);
            Assert.Equal("vendor/chat", body.GetProperty("model").GetString());
            Assert.Equal("Hi", body.GetProperty("messages")[0].GetProperty("content").GetString());
            foreach (var (name, value) in options.AdditionalProperties)
                Assert.Equal(value.GetRawText(), body.GetProperty(name).GetRawText());
            return JsonResponse(Completion);
        });

        var result = Element(await provider.CompleteChatAsync(options));
        Assert.Equal("gwdg/vendor/chat", result.GetProperty("model").GetString());
        Assert.Equal("Hello", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(2, result.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(3, result.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal(original, JsonSerializer.Serialize(options.AdditionalProperties));
    }

    [Fact]
    public async Task NativeStreamingPreservesTextAndTrailingUsage()
    {
        var provider = CreateProvider(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://chat-ai.academiccloud.de/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            AssertAuth(request);
            var body = await ReadBody(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal("low", body.GetProperty("reasoning_effort").GetString());
            return StreamResponse();
        });
        var options = ChatOptions();
        options.Stream = true;
        options.AdditionalProperties = new() { ["reasoning_effort"] = Element("low") };
        var updates = new List<JsonElement>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            updates.Add(Element(update));
        Assert.Contains(updates, update => update.GetProperty("choices").EnumerateArray().Any(choice =>
            choice.GetProperty("delta").TryGetProperty("content", out var content) && content.GetString() == "Hello"));
        var usage = Assert.Single(updates, update => update.TryGetProperty("usage", out var value) &&
            value.ValueKind == JsonValueKind.Object).GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(3, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task UnifiedMessagesAndResponsesUseStandardChatAdapters()
    {
        var calls = 0;
        var provider = CreateProvider((request, _) =>
        {
            calls++;
            Assert.Equal("https://chat-ai.academiccloud.de/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            AssertAuth(request);
            return Task.FromResult(JsonResponse(Completion));
        });
        var unified = await provider.ExecuteUnifiedAsync(new() { ProviderId = "gwdg", Model = "vendor/chat" });
        Assert.Contains("Hello", JsonSerializer.Serialize(unified));
        var response = await provider.ResponsesAsync(new ResponseRequest { Model = "vendor/chat", Input = "Hi" });
        Assert.Equal("completed", response.Status);
        Assert.Contains("Hello", JsonSerializer.Serialize(response));
        var message = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "vendor/chat", MaxTokens = 20, Messages = []
        }, []);
        Assert.Contains("Hello", JsonSerializer.Serialize(message));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task UnifiedStreamMapsTrailingUsageAndUIUsesChatStream()
    {
        var calls = 0;
        var provider = CreateProvider((request, _) =>
        {
            calls++;
            Assert.Equal("https://chat-ai.academiccloud.de/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            AssertAuth(request);
            return Task.FromResult(StreamResponse());
        });
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(new() { ProviderId = "gwdg", Model = "vendor/chat" }))
            events.Add(item);
        Assert.Contains(events, item => item.Event.Type == "text-delta");
        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(events, item => item.Event.Type == "finish").Event.Data);
        Assert.Equal(2, finish.InputTokens);
        Assert.Equal(3, finish.OutputTokens);
        Assert.Equal(5, finish.TotalTokens);

        var ui = JsonSerializer.Deserialize<ChatRequest>("""
            {"id":"chat-1","model":"gwdg/vendor/chat","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}]}
            """, JsonSerializerOptions.Web)!;
        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(ui))
            parts.Add(part);
        Assert.Equal("Hello", string.Concat(parts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta)));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeChatErrorsIncludeProviderMessage(bool streaming)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"error":{"message":"native chat detail","type":"rate_limit_error"}}""", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (streaming)
                await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions())) { }
            else
                await provider.CompleteChatAsync(ChatOptions());
        });
        Assert.Contains("native chat detail", error.Message);
    }

    [Theory]
    [InlineData("discovery", false)]
    [InlineData("chat", false)]
    [InlineData("stream", false)]
    [InlineData("discovery", true)]
    [InlineData("chat", true)]
    [InlineData("stream", true)]
    public async Task CancellationPropagatesBeforeAndDuringRequests(string operation, bool inflight)
    {
        using var cancellation = new CancellationTokenSource();
        if (!inflight)
            cancellation.Cancel();
        var provider = CreateProvider(async (_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            if (inflight)
                cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "discovery":
                    await provider.ListModels(cancellation.Token);
                    break;
                case "chat":
                    await provider.CompleteChatAsync(ChatOptions(), cancellation.Token);
                    break;
                default:
                    await foreach (var _ in provider.CompleteChatStreamingAsync(ChatOptions(), cancellation.Token)) { }
                    break;
            }
        });
    }

    [Fact]
    public async Task EmbeddingsAndAudioAreNotAdvertisedOrExecuted()
    {
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(
            new() { Model = "embedding", Input = Element("Hi") }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.EmbeddingRequestAsync(
            new() { Model = "embedding", Values = ["Hi"] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(
            new() { Model = "speech", Text = "Hi" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(
            new() { Model = "speech", Input = "Hi" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAITranscriptionRequestAsync(new()));
    }

    [Fact]
    public void HeaderScopedProviderIsRegisteredInRegistry()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-GWDG-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("X-GWDG-Key", HeaderApiKeyResolver.SupportedProviderHeaders["gwdg"]);
        Assert.Equal("header-key", resolver.Resolve("GWDG"));
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            item => item.ImplementationType == typeof(GWDGProvider)).Lifetime);
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("gwdg"));
        var provider = Assert.IsType<GWDGProvider>(registry.GetModelProvider(scope.ServiceProvider, "gwdg"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<GWDGProvider>(), provider);
    }

    private static ChatCompletionOptions ChatOptions() => new()
    {
        Model = "vendor/chat", Messages = [new() { Role = "user", Content = Element("Hi") }]
    };

    private static GWDGProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, KeyResolver? key = null)
        => new(key ?? new("test-key"), new Factory(responder),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> ReadBody(HttpRequestMessage request, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage StreamResponse()
        => new(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
    private static void AssertAuth(HttpRequestMessage request)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider) => provider == "gwdg" ? Key : null;
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
