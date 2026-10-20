using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Yandex;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Yandex;

public sealed class YandexProviderTests
{
    private const string ChatModel = "gpt://folder/yandexgpt/latest";
    private const string EmbeddingModel = "emb://folder/text-search-doc/latest";
    private const string Catalog = """
        {"data":[
          {"id":"gpt://folder/yandexgpt/latest","owned_by":"yandex","created":1720000000,"native":{"value":7}},
          {"id":"emb://folder/text-search-doc/latest","owned_by":"yandex"},
          {"id":"gpt://folder/tenant-model/version","owned_by":"tenant"},
          {"id":"gpt://folder/yandexgpt/latest"},
          {"id":"cls://folder/classifier/latest"},{"id":"unknown-model"},
          {"id":""},{"id":null},{},null,42
        ]}
        """;
    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"gpt://folder/yandexgpt/latest",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Привет"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;
    private const string Stream = """
        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt://folder/yandexgpt/latest","choices":[{"index":0,"delta":{"content":"Привет"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt://folder/yandexgpt/latest","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt://folder/yandexgpt/latest","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

        data: [DONE]

        """;
    private const string Embeddings = """
        {"object":"list","model":"emb://folder/text-search-doc/latest",
         "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
         "usage":{"prompt_tokens":2,"total_tokens":2}}
        """;

    [Fact]
    public async Task DiscoveryUsesNativeProjectAndScopesCacheByKeyAndFolder()
    {
        var calls = 0;
        var keys = new Keys("first-key");
        var context = Context();
        var provider = Create((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://llm.api.cloud.yandex.net/v1/models", request.RequestUri!.AbsoluteUri);
            AssertAuth(request, keys.Key!);
            Assert.Equal(context.HttpContext!.Request.Headers["OpenAI-Project"].ToString(),
                Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            return Task.FromResult(Json(Catalog));
        }, keys, context);

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(3, models.Length);
        Assert.Equal("yandex/" + ChatModel, models[0].Id);
        Assert.Equal(ChatModel, models[0].Id.SplitModelId().Model);
        Assert.Equal(ChatModel, models[0].Name);
        Assert.Equal("language", models[0].Type);
        Assert.Equal("yandex", models[0].OwnedBy);
        Assert.Equal(1720000000, models[0].Created);
        Assert.Equal(7, models[0].Metadata!["native"].GetProperty("value").GetInt32());
        Assert.Equal("yandex/" + EmbeddingModel, models[1].Id);
        Assert.Equal("embedding", models[1].Type);
        Assert.Equal("yandex/gpt://folder/tenant-model/version", models[2].Id);
        await provider.ListModels();
        Assert.Equal(1, calls);
        context.HttpContext!.Request.Headers["OpenAI-Project"] = "another-folder";
        await provider.ListModels();
        Assert.Equal(2, calls);
        keys.Key = "second-key";
        await provider.ListModels();
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(null, "folder")]
    [InlineData("", "folder")]
    [InlineData(" ", "folder")]
    [InlineData("test-key", null)]
    [InlineData("test-key", "")]
    [InlineData("test-key", " ")]
    public async Task DiscoveryWithoutKeyOrProjectDoesNotCallApi(string? key, string? folder)
        => Assert.Empty(await Create(NoNetwork, new(key), Context(folder)).ListModels());

    [Fact]
    public async Task DiscoveryWithoutHttpContextIsEmpty()
        => Assert.Empty(await Create(NoNetwork, context: new HttpContextAccessor()).ListModels());

    [Fact]
    public async Task MissingKeyRejectsInference()
    {
        var provider = Create(NoNetwork, new(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(Options()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(Options())) { }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIEmbeddingRequestAsync(new() { Model = EmbeddingModel, Input = Element("hello") }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{")]
    public async Task InvalidDiscoveryFailsWithoutInventedFallback(string body)
        => await Assert.ThrowsAnyAsync<JsonException>(() => Create((_, _) =>
            Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task EmptyDiscoveryStaysEmpty()
        => Assert.Empty(await Create((_, _) => Task.FromResult(Json("""{"data":[]}"""))).ListModels());

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DiscoveryPreservesStatusAndDoesNotCacheErrors(HttpStatusCode status)
    {
        var calls = 0;
        var provider = Create((_, _) => Task.FromResult(++calls == 1
            ? Json("""{"error":{"message":"native discovery detail"}}""", status) : Json(Catalog)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("native discovery detail", error.Message);
        Assert.Equal(3, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DiscoveredChatModelAndNativeOptionsAreUsable()
    {
        var options = Options();
        options.AdditionalProperties = new()
        {
            ["reasoning_effort"] = Element("high"),
            ["future_option"] = Element(new { nested = new object[] { true, "native", 42 } })
        };
        var original = JsonSerializer.Serialize(options.AdditionalProperties);
        var provider = Create(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(Catalog);
            AssertEndpoint(request, "chat/completions");
            Assert.Equal("folder", Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            var body = await Body(request, token);
            Assert.Equal(ChatModel, body.GetProperty("model").GetString());
            Assert.Equal("Привет", body.GetProperty("messages")[0].GetProperty("content").GetString());
            foreach (var (name, value) in options.AdditionalProperties)
                Assert.Equal(value.GetRawText(), body.GetProperty(name).GetRawText());
            return Json(Completion);
        });
        options.Model = (await provider.ListModels()).First().Id.SplitModelId().Model;
        var result = Element(await provider.CompleteChatAsync(options));
        Assert.Equal("yandex/" + ChatModel, result.GetProperty("model").GetString());
        Assert.Equal("Привет", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(5, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal(original, JsonSerializer.Serialize(options.AdditionalProperties));
    }

    [Fact]
    public async Task StreamingPreservesUsageAndNativeOptions()
    {
        var provider = Create(async (request, token) =>
        {
            AssertEndpoint(request, "chat/completions");
            var body = await Body(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            return Sse();
        });
        var options = Options();
        options.Stream = true;
        options.AdditionalProperties = new() { ["reasoning_effort"] = Element("high") };
        var updates = new List<JsonElement>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            updates.Add(Element(update));
        Assert.Equal("Привет", updates[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(5, updates.Single(update => update.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object).GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task StandardChatAdaptersUseNativeModelUriAndProviderMetadata()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertEndpoint(request, "chat/completions");
            var body = await Body(request, token);
            Assert.Equal(ChatModel, body.GetProperty("model").GetString());
            if (calls == 1)
                Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            return body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True
                ? Sse() : Json(Completion);
        });
        var unified = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "yandex", Model = ChatModel,
            Metadata = new() { ["yandex"] = Element(new { reasoning_effort = "high" }) }
        });
        Assert.Equal(5, unified.NormalizedUsage!.TotalTokens);
        Assert.Equal("completed", (await provider.ResponsesAsync(new ResponseRequest { Model = ChatModel, Input = "Hi" })).Status);
        Assert.NotNull(await provider.MessagesAsync(new MessagesRequest { Model = ChatModel, MaxTokens = 20, Messages = [] }, []));
        var events = new List<AIStreamEvent>();
        await foreach (var part in provider.StreamUnifiedAsync(new() { ProviderId = "yandex", Model = ChatModel }))
            events.Add(part);
        Assert.Contains(events, part => part.Event.Type == "text-delta");
        var chat = JsonSerializer.Deserialize<ChatRequest>($$"""
            {"id":"chat-1","model":"yandex/{{ChatModel}}",
             "messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}]}
            """, JsonSerializerOptions.Web)!;
        var uiParts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(chat))
            uiParts.Add(part);
        Assert.Equal("Привет", string.Concat(uiParts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta)));
        var messageParts = new List<MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new() { Model = ChatModel, MaxTokens = 20, Messages = [] }, []))
            messageParts.Add(part);
        Assert.NotEmpty(messageParts);
        var responseParts = new List<AIHappey.Responses.Streaming.ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new() { Model = ChatModel, Input = "Hi" }))
            responseParts.Add(part);
        Assert.NotEmpty(responseParts);
        Assert.Equal(7, calls);
    }

    [Fact]
    public async Task EmbeddingsUseDiscoveredUriAndPreserveParametersVectorsAndUsage()
    {
        var provider = Create(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(Catalog);
            AssertEndpoint(request, "embeddings");
            var body = await Body(request, token);
            Assert.Equal(EmbeddingModel, body.GetProperty("model").GetString());
            Assert.Equal("hello", body.GetProperty("input").GetString());
            Assert.Equal(2, body.GetProperty("dimensions").GetInt32());
            Assert.Equal("float", body.GetProperty("encoding_format").GetString());
            Assert.True(body.GetProperty("future_option").GetProperty("nested").GetBoolean());
            return Json(Embeddings);
        });
        var model = (await provider.ListModels()).Single(model => model.Type == "embedding").Id;
        var options = new OpenAIEmbeddingRequest
        {
            Model = model, Input = Element(new[] { "hello" }), Dimensions = 2, EncodingFormat = "float",
            AdditionalProperties = new() { ["future_option"] = Element(new { nested = true }) }
        };
        var result = Element(await provider.OpenAIEmbeddingRequestAsync(options));
        Assert.Equal(model, result.GetProperty("model").GetString());
        Assert.Equal(0.1, result.GetProperty("data")[0].GetProperty("embedding")[0].GetDouble());
        Assert.Equal(2, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal(model, options.Model);
        var normalized = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = model, Values = ["hello"],
            ProviderOptions = new() { ["yandex"] = Element(new { dimensions = 2, future_option = new { nested = true } }) }
        });
        Assert.Equal(2, normalized.Usage!.Tokens);
        Assert.Equal(new float[] { 0.1f, 0.2f }, Assert.Single(normalized.Embeddings));
    }

    [Fact]
    public async Task EmbeddingBatchesUseScalarRequestsAndAggregateInOrder()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            AssertEndpoint(request, "embeddings");
            var body = await Body(request, token);
            Assert.Equal(calls++ == 0 ? "first" : "second", body.GetProperty("input").GetString());
            Assert.Equal("float", body.GetProperty("encoding_format").GetString());
            return Json($$"""
                {"object":"list","model":"{{EmbeddingModel}}",
                 "data":[{"object":"embedding","index":0,"embedding":[{{calls}}]}],
                 "usage":{"prompt_tokens":{{calls}},"total_tokens":{{calls}}}}
                """);
        });
        var response = await provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = EmbeddingModel, Input = Element(new[] { "first", "second" })
        });
        var data = response.Data.ToArray();
        Assert.Equal(2, calls);
        Assert.Equal(new[] { 0, 1 }, data.Select(item => item.Index));
        Assert.Equal(1, data[0].Embedding[0].GetInt32());
        Assert.Equal(2, data[1].Embedding[0].GetInt32());
        Assert.Equal(3, response.Usage.PromptTokens);
        Assert.Equal(3, response.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"embedding":[1]},{"embedding":[2]}]}""")]
    public async Task EmbeddingsRejectWrongVectorCount(string body)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Create((_, _) =>
            Task.FromResult(Json(body))).OpenAIEmbeddingRequestAsync(new()
            {
                Model = EmbeddingModel, Input = Element("hello")
            }));

    [Fact]
    public async Task EmbeddingsForwardExplicitEncodingWithoutSubstitution()
    {
        var provider = Create(async (request, token) =>
        {
            var body = await Body(request, token);
            Assert.Equal("base64", body.GetProperty("encoding_format").GetString());
            return Json("""{"error":{"message":"unsupported encoding"}}""", HttpStatusCode.BadRequest);
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = EmbeddingModel, Input = Element("hello"), EncodingFormat = "base64"
        }));
        Assert.Contains("unsupported encoding", error.Message);
    }

    [Fact]
    public async Task ChatToolCallsAndStructuredOutputPassThrough()
    {
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>($$"""
            {"model":"{{ChatModel}}","messages":[{"role":"user","content":"Weather?"}],
             "tools":[{"type":"function","function":{"name":"weather","parameters":{"type":"object","properties":{}}}}],
             "tool_choice":"auto","response_format":{"type":"json_object"}}
            """, JsonSerializerOptions.Web)!;
        var provider = Create(async (request, token) =>
        {
            var body = await Body(request, token);
            Assert.Equal("weather", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("auto", body.GetProperty("tool_choice").GetString());
            Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
            return Json($$"""
                {"id":"chat-tools","object":"chat.completion","created":1,"model":"{{ChatModel}}",
                 "choices":[{"index":0,"message":{"role":"assistant","content":null,
                   "tool_calls":[{"id":"call-1","type":"function","function":{"name":"weather","arguments":"{}"}}]},
                   "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
                """);
        });
        var result = Element(await provider.CompleteChatAsync(options));
        Assert.Equal("tool_calls", result.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal("weather", result.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("stream")]
    [InlineData("embedding")]
    public async Task InferenceErrorsPreserveNativeDetails(string operation)
    {
        var provider = Create((_, _) => Task.FromResult(Json(
            """{"error":{"message":"native inference detail"}}""", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(provider, operation));
        Assert.Contains("native inference detail", error.Message);
    }

    [Theory]
    [InlineData("discovery", false)]
    [InlineData("chat", false)]
    [InlineData("stream", false)]
    [InlineData("embedding", false)]
    [InlineData("discovery", true)]
    [InlineData("chat", true)]
    [InlineData("stream", true)]
    [InlineData("embedding", true)]
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(provider, operation, cancellation.Token));
    }

    [Fact]
    public void HeaderAndScopedProviderRegistrationAreAvailable()
    {
        var context = Context();
        context.HttpContext!.Request.Headers["X-Yandex-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(context));
        Assert.Equal("X-Yandex-Key", HeaderApiKeyResolver.SupportedProviderHeaders["yandex"]);
        Assert.Equal("header-key", resolver.Resolve("YANDEX"));
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            descriptor => descriptor.ImplementationType == typeof(YandexProvider)).Lifetime);
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpContextAccessor>(context);
        services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("yandex"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<YandexProvider>(),
            registry.GetModelProvider(scope.ServiceProvider, "yandex"));
    }

    private static async Task Invoke(YandexProvider provider, string operation, CancellationToken token = default)
    {
        switch (operation)
        {
            case "discovery": await provider.ListModels(token); break;
            case "chat": await provider.CompleteChatAsync(Options(), token); break;
            case "embedding":
                await provider.OpenAIEmbeddingRequestAsync(new() { Model = EmbeddingModel, Input = Element("hello") }, token);
                break;
            default:
                await foreach (var _ in provider.CompleteChatStreamingAsync(Options(), token)) { }
                break;
        }
    }

    private static ChatCompletionOptions Options() => new()
    {
        Model = ChatModel, Messages = [new() { Role = "user", Content = Element("Привет") }]
    };
    private static HttpContextAccessor Context(string? folder = "folder")
    {
        var context = new DefaultHttpContext();
        if (folder is not null)
            context.Request.Headers["OpenAI-Project"] = folder;
        return new() { HttpContext = context };
    }
    private static YandexProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        Keys? keys = null, HttpContextAccessor? context = null)
        => new(keys ?? new("test-key"), new Factory(responder),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), context ?? Context());
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse()
        => new(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
    private static void AssertEndpoint(HttpRequestMessage request, string endpoint)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://llm.api.cloud.yandex.net/v1/" + endpoint, request.RequestUri!.AbsoluteUri);
        AssertAuth(request);
    }
    private static void AssertAuth(HttpRequestMessage request, string key = "test-key")
    {
        Assert.Equal("Api-Key", request.Headers.Authorization?.Scheme);
        Assert.Equal(key, request.Headers.Authorization?.Parameter);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider) => provider == "yandex" ? Key : null;
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
