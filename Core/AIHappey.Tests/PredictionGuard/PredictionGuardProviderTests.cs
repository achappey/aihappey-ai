using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.PredictionGuard;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.PredictionGuard;

public sealed class PredictionGuardProviderTests
{
    private const string Catalog = """
        {"data":[
          {"id":"tenant/chat","created":123,"owned_by":"native-owner","description":"Native description",
           "max_context_length":12345,"capabilities":{"chat_completion":true,"tool_calling":true}},
          {"id":"embed","capabilities":{"embedding":true}},
          {"id":"rank","capabilities":{"rerank":true}},
          {"id":"vision","capabilities":{"chat_with_image":true}},
          {"id":"tokenize-only","capabilities":{"tokenize":true}},
          {"id":"image-embed-only","capabilities":{"embedding_with_image":true}},
          {"id":"unknown"},{"id":"false","capabilities":{"chat_completion":false}},
          {"id":"string-flag","capabilities":{"chat_completion":"true"}},
          {"id":"tenant/chat","capabilities":{"chat_completion":true}},
          {"id":""},{},null,42
        ]}
        """;
    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"tenant/chat",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello","reasoning":"Thought"},
                     "finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;
    private const string Stream = """
        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"tenant/chat","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"tenant/chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"tenant/chat","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

        """;
    private const string Embeddings = """
        {"id":"emb-1","model":"embed","data":[{"index":1,"embedding":[0.3,0.4]},{"index":0,"embedding":[0.1,0.2]}]}
        """;
    private const string Rankings = """
        {"model":"rank","results":[{"index":0,"relevance_score":0.2,"text":"first"},
                                 {"index":1,"relevance_score":0.8,"text":"second"}]}
        """;

    [Fact]
    public async Task DiscoveryFiltersCapabilitiesMapsCatalogFieldsAndCachesPerCredential()
    {
        var calls = 0;
        var keys = new Keys("first-key");
        var provider = Create((request, _) =>
        {
            calls++;
            AssertRequest(request, "models", HttpMethod.Get, keys.Key!);
            return Task.FromResult(Json(Catalog));
        }, keys);
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["predictionguard/tenant/chat", "predictionguard/embed", "predictionguard/rank",
            "predictionguard/vision"], models.Select(model => model.Id));
        Assert.Equal(["language", "embedding", "reranking", "language"], models.Select(model => model.Type));
        Assert.Equal("native-owner", models[0].OwnedBy);
        Assert.Equal(123, models[0].Created);
        Assert.Equal(4, (await provider.ListModels()).Count());
        Assert.Equal(1, calls);
        keys.Key = "second-key";
        Assert.Equal(4, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{")]
    public async Task InvalidDiscoveryDoesNotInventModels(string body)
        => await Assert.ThrowsAnyAsync<JsonException>(() =>
            Create((_, _) => Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task EmptyDiscoveryRemainsEmpty()
        => Assert.Empty(await Create((_, _) => Task.FromResult(Json("""{"data":[]}"""))).ListModels());

    [Fact]
    public async Task DiscoveryErrorsPreserveStatusAndAreNotCached()
    {
        var calls = 0;
        var provider = Create((_, _) => Task.FromResult(++calls == 1
            ? Json("""{"error":"native failure"}""", HttpStatusCode.Forbidden) : Json(Catalog)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("native failure", error.Message);
        Assert.Equal(4, (await provider.ListModels()).Count());
    }

    [Fact]
    public async Task DiscoveredChatModelAndNativeSafetyOptionsRoundTrip()
    {
        var provider = Create(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(Catalog);
            AssertRequest(request, "chat/completions");
            var body = await Body(request, token);
            Assert.Equal("tenant/chat", body.GetProperty("model").GetString());
            Assert.Equal("replace", body.GetProperty("input").GetProperty("pii").GetString());
            Assert.True(body.GetProperty("output").GetProperty("toxicity").GetBoolean());
            Assert.Equal(9, body.GetProperty("future_option").GetProperty("nested").GetInt32());
            return Json(Completion);
        });
        var options = Options();
        options.Model = (await provider.ListModels()).First().Id;
        options.AdditionalProperties = new() { ["input"] = Element(new { pii = "replace" }) };
        options.Metadata = new() { ["predictionguard"] = Element(new { output = new { toxicity = true }, future_option = new { nested = 9 } }) };
        var result = Element(await provider.CompleteChatAsync(options));
        Assert.Equal("predictionguard/tenant/chat", result.GetProperty("model").GetString());
        Assert.Equal("Hello", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("Thought", result.GetProperty("choices")[0].GetProperty("message").GetProperty("reasoning").GetString());
        Assert.Equal(5, result.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal("predictionguard/tenant/chat", options.Model);
    }

    [Fact]
    public async Task StreamingForwardsNativeOptionsAndReportsUsageWithoutDoneSentinel()
    {
        var provider = Create(async (request, token) =>
        {
            AssertRequest(request, "chat/completions");
            var body = await Body(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal(7, body.GetProperty("top_k").GetInt32());
            return Sse();
        });
        var options = Options();
        options.AdditionalProperties = new()
        {
            ["top_k"] = Element(7), ["stream_options"] = Element(new { include_usage = true })
        };
        var updates = new List<JsonElement>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            updates.Add(Element(update));
        Assert.Equal("Hello", updates[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(5, updates.Last().GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task ExistingChatAdaptersPreserveProviderOptionsAndUsage()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertRequest(request, "chat/completions");
            var body = await Body(request, token);
            if (body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
                return Sse();
            if (calls == 1)
                Assert.Equal("replace", body.GetProperty("input").GetProperty("pii").GetString());
            return Json(Completion);
        });
        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "predictionguard", Model = "tenant/chat",
            Metadata = new() { ["predictionguard"] = Element(new { input = new { pii = "replace" } }) }
        });
        Assert.Equal(5, response.NormalizedUsage!.TotalTokens);
        Assert.Equal("completed", (await provider.ResponsesAsync(new ResponseRequest { Model = "tenant/chat", Input = "Hi" })).Status);
        Assert.NotNull(await provider.MessagesAsync(new MessagesRequest { Model = "tenant/chat", MaxTokens = 20, Messages = [] }, []));
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(new() { ProviderId = "predictionguard", Model = "tenant/chat" }))
            events.Add(item);
        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(events, item => item.Event.Type == "finish").Event.Data);
        Assert.Equal(5, finish.TotalTokens);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task StreamingAdaptersUseChatAndVercelForwardsProviderMetadata()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertRequest(request, "chat/completions");
            var body = await Body(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            if (calls == 1)
                Assert.Equal(11, body.GetProperty("top_k").GetInt32());
            return Sse();
        });
        var chat = JsonSerializer.Deserialize<ChatRequest>("""
            {"id":"chat-1","model":"predictionguard/tenant/chat",
             "messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}],
             "providerMetadata":{"predictionguard":{"top_k":11}}}
            """, JsonSerializerOptions.Web)!;
        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(chat))
            parts.Add(part);
        Assert.Equal("Hello", string.Concat(parts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta)));
        var messages = new List<MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new MessagesRequest
        {
            Model = "tenant/chat", MaxTokens = 20, Messages = []
        }, []))
            messages.Add(part);
        Assert.NotEmpty(messages);
        var responses = new List<AIHappey.Responses.Streaming.ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new ResponseRequest { Model = "tenant/chat", Input = "Hi" }))
            responses.Add(part);
        Assert.NotEmpty(responses);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ToolsAndVisionUseExistingChatContractWithoutNativeRemapping()
    {
        var provider = Create(async (request, token) =>
        {
            var body = await Body(request, token);
            Assert.Equal("image_url", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            return Json("""
                {"id":"tool-1","model":"tenant/chat","choices":[{"index":0,"finish_reason":"tool_calls",
                "message":{"role":"assistant","content":null,"tool_calls":[
                {"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"query\":\"test\"}"}}]}}]}
                """);
        });
        var options = Options();
        options.Messages = [new()
        {
            Role = "user", Content = Element(new object[]
            {
                new { type = "text", text = "Describe" },
                new { type = "image_url", image_url = new { url = "data:image/jpeg;base64,AA==" } }
            })
        }];
        options.Tools = [new { type = "function", function = new { name = "lookup", parameters = new { type = "object" } } }];
        var response = Element(await provider.CompleteChatAsync(options));
        Assert.Equal("call-1", response.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task EmbeddingsMapInputsNativeOptionsOrderingAndUnreportedUsage()
    {
        var provider = Create(async (request, token) =>
        {
            AssertRequest(request, "embeddings");
            var body = await Body(request, token);
            Assert.Equal("embed", body.GetProperty("model").GetString());
            Assert.Equal(2, body.GetProperty("input").GetArrayLength());
            Assert.False(body.TryGetProperty("encoding_format", out _));
            Assert.True(body.GetProperty("truncate").GetBoolean());
            Assert.Equal("Left", body.GetProperty("truncation_direction").GetString());
            return Json(Embeddings);
        });
        var native = new Dictionary<string, JsonElement> { ["truncate"] = Element(true), ["truncation_direction"] = Element("Left") };
        var openai = await provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = "predictionguard/embed", Input = Element(new[] { "a", "b" }), AdditionalProperties = native
        });
        Assert.Equal("predictionguard/embed", openai.Model);
        Assert.Null(openai.Usage);
        var result = await provider.EmbeddingRequestAsync(new()
        {
            Model = "predictionguard/embed", Values = ["a", "b"],
            ProviderOptions = new() { ["predictionguard"] = Element(native) }
        });
        Assert.Equal([0.1f, 0.2f], result.Embeddings.First());
        Assert.Null(result.Usage);
        Assert.NotNull(result.Response!.Body);
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("[1,2]")]
    [InlineData("[[1,2]]")]
    public async Task EmbeddingStandardInputsAndReturnedUsageArePreserved(string input)
    {
        var provider = Create((_, _) => Task.FromResult(Json("""
            {"model":"embed","data":[{"index":0,"embedding":[0.1]}],"usage":{"prompt_tokens":3,"total_tokens":3}}
            """)));
        var response = await provider.OpenAIEmbeddingRequestAsync(new() { Model = "embed", Input = JsonSerializer.Deserialize<JsonElement>(input) });
        Assert.Equal(3, response.Usage.PromptTokens);
    }

    [Theory]
    [InlineData("base64", null, null)]
    [InlineData("float", 2, null)]
    [InlineData(null, null, "user")]
    public async Task UnsupportedEmbeddingOptionsFailBeforeNetwork(string? format, int? dimensions, string? user)
        => await Assert.ThrowsAsync<NotSupportedException>(() => Create(NoNetwork).OpenAIEmbeddingRequestAsync(new()
        {
            Model = "embed", Input = Element("text"), EncodingFormat = format, Dimensions = dimensions, User = user
        }));

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[]}]}")]
    [InlineData("{\"data\":[{\"index\":1,\"embedding\":[0.1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[\"bad\"]}]}")]
    public async Task InvalidEmbeddingResponseFails(string body)
        => await Assert.ThrowsAnyAsync<JsonException>(() =>
            Create((_, _) => Task.FromResult(Json(body))).OpenAIEmbeddingRequestAsync(new() { Model = "embed", Input = Element("text") }));

    [Fact]
    public async Task RerankPassesNativeFieldsAndAppliesStandardTopNLocally()
    {
        var provider = Create(async (request, token) =>
        {
            AssertRequest(request, "rerank");
            var body = await Body(request, token);
            Assert.Equal("rank", body.GetProperty("model").GetString());
            Assert.Equal("query", body.GetProperty("query").GetString());
            Assert.Equal(2, body.GetProperty("documents").GetArrayLength());
            Assert.True(body.GetProperty("return_documents").GetBoolean());
            Assert.Equal(42, body.GetProperty("future").GetInt32());
            Assert.False(body.TryGetProperty("top_n", out _));
            return Json(Rankings);
        });
        var response = await provider.RerankingRequest(Rerank());
        var ranking = Assert.Single(response.Ranking);
        Assert.Equal(1, ranking.Index);
        Assert.Equal(0.8f, ranking.RelevanceScore);
        Assert.Equal("predictionguard/rank", response.Response.ModelId);
        Assert.Equal(2, ((JsonElement)response.Response.Body!).GetProperty("results").GetArrayLength());
    }

    [Theory]
    [InlineData("{\"results\":[{\"index\":2,\"relevance_score\":0.5}]}")]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":0.5},{\"index\":0,\"relevance_score\":0.6}]}")]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":\"bad\"}]}")]
    [InlineData("{}")]
    public async Task InvalidRerankingResponseFails(string body)
        => await Assert.ThrowsAnyAsync<JsonException>(() =>
            Create((_, _) => Task.FromResult(Json(body))).RerankingRequest(Rerank()));

    [Theory]
    [InlineData("chat")]
    [InlineData("discovery")]
    [InlineData("stream")]
    [InlineData("embeddings")]
    [InlineData("rerank")]
    public async Task ErrorsAndCancellationPropagate(string operation)
    {
        var provider = Create((_, _) => Task.FromResult(Json("""{"error":"native detail"}""", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Invoke(provider, operation));
        Assert.Contains("native detail", error.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(Create(NoNetwork), operation, cancellation.Token));
        using var pendingCancellation = new CancellationTokenSource();
        var pending = Create(async (_, token) =>
        {
            pendingCancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(pending, operation, pendingCancellation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingCredentialsNeverSendRequests(string? key)
    {
        var provider = Create(NoNetwork, new(key));
        Assert.Empty(await provider.ListModels());
        foreach (var operation in new[] { "chat", "stream", "embeddings", "rerank" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, operation));
    }

    [Fact]
    public void HeaderRegistrationAndProviderRegistryAreAvailable()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-PredictionGuard-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("X-PredictionGuard-Key", HeaderApiKeyResolver.SupportedProviderHeaders["predictionguard"]);
        Assert.Equal("header-key", resolver.Resolve("PREDICTIONGUARD"));
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            descriptor => descriptor.ImplementationType == typeof(PredictionGuardProvider)).Lifetime);
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("predictionguard"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<PredictionGuardProvider>(),
            registry.GetModelProvider(scope.ServiceProvider, "predictionguard"));
    }

    private static async Task Invoke(PredictionGuardProvider provider, string operation, CancellationToken token = default)
    {
        switch (operation)
        {
            case "discovery": await provider.ListModels(token); break;
            case "chat": await provider.CompleteChatAsync(Options(), token); break;
            case "stream": await foreach (var _ in provider.CompleteChatStreamingAsync(Options(), token)) { } break;
            case "embeddings": await provider.OpenAIEmbeddingRequestAsync(new() { Model = "embed", Input = Element("text") }, token); break;
            default: await provider.RerankingRequest(Rerank(), token); break;
        }
    }
    private static ChatCompletionOptions Options() => new()
    {
        Model = "tenant/chat", Messages = [new() { Role = "user", Content = Element("Hi") }]
    };
    private static RerankingRequest Rerank() => new()
    {
        Model = "predictionguard/rank", Query = "query", TopN = 1,
        Documents = new() { Type = "text", Values = Element(new[] { "first", "second" }) },
        ProviderOptions = new() { ["predictionguard"] = Element(new { return_documents = true, future = 42 }) }
    };
    private static PredictionGuardProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, Keys? keys = null)
        => new(keys ?? new("test-key"), new Factory(responder),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse()
        => new(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
    private static void AssertRequest(HttpRequestMessage request, string path, HttpMethod? method = null, string key = "test-key")
    {
        Assert.Equal(method ?? HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.predictionguard.com/{path}", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(key, request.Headers.Authorization?.Parameter);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider) => provider == "predictionguard" ? Key : null;
    }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
