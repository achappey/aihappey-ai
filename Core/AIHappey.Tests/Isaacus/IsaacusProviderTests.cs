using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Isaacus;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Isaacus;

public sealed class IsaacusProviderTests
{
    private const string EmbeddingSuccess = """
        {"embeddings":[{"index":0,"embedding":[1,2]}],"usage":{"input_tokens":7},"extra":"metadata"}
        """;
    private const string RerankingSuccess = """
        {"results":[{"index":1,"score":0.9},{"index":0,"score":0.2}],"usage":{"input_tokens":11},"extra":"metadata"}
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-key")]
    public async Task DiscoveryUsesThreeDocumentedModelsWithoutNetworkOrCredentials(string? key)
    {
        var provider = CreateProvider(NoNetwork, key);
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal("isaacus", provider.GetIdentifier());
        Assert.Equal(["isaacus/kanon-2-embedder", "isaacus/kanon-2-reranker", "isaacus/kanon-universal-classifier"],
            models.Select(model => model.Id));
        Assert.Equal(["embedding", "reranking", "reranking"], models.Select(model => model.Type));
        Assert.All(models, model =>
        {
            Assert.Equal("Isaacus", model.OwnedBy);
            Assert.Equal("model", model.Object);
            Assert.Equal(model.Id["isaacus/".Length..], model.Name);
            Assert.Null(model.ContextWindow);
            Assert.Null(model.MaxTokens);
            Assert.Null(model.Created);
            Assert.Null(model.Pricing);
            Assert.Null(model.Metadata);
        });
    }

    [Theory]
    [InlineData("isaacus/kanon-2-embedder", "kanon-2-embedder")]
    [InlineData("kanon-2-embedder", "kanon-2-embedder")]
    [InlineData("ISAACUS/namespace/model", "namespace/model")]
    [InlineData("namespace/model", "namespace/model")]
    public async Task OpenAIMapsNativeEmbeddingFieldsAndUsage(string model, string expected)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            AssertTransport(message, "embeddings");
            var body = await ReadBody(message, token);
            Assert.Equal(expected, body.GetProperty("model").GetString());
            Assert.Equal(["hello"], body.GetProperty("texts").Deserialize<string[]>());
            Assert.Equal(128, body.GetProperty("dimensions").GetInt32());
            Assert.Equal("retrieval/query", body.GetProperty("task").GetString());
            Assert.Equal("drop_end", body.GetProperty("overflow_strategy").GetString());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("input", out _));
            Assert.False(body.TryGetProperty("encoding_format", out _));
            Assert.False(body.TryGetProperty("user", out _));
            return JsonResponse(EmbeddingSuccess);
        });
        var request = EmbeddingRequest(model);
        request.Dimensions = 128;
        request.AdditionalProperties = new()
        {
            ["model"] = Element("wrong-model"),
            ["texts"] = Element(new[] { "wrong-text" }),
            ["dimensions"] = Element(64),
            ["task"] = Element("retrieval/query"),
            ["overflow_strategy"] = Element("drop_end"),
            ["future_option"] = Element(new { enabled = true })
        };
        var response = await provider.OpenAIEmbeddingRequestAsync(request);
        Assert.Equal($"isaacus/{expected}", response.Model);
        Assert.Equal("list", response.Object);
        Assert.Equal("embedding", response.Data.Single().Object);
        Assert.Equal(0, response.Data.Single().Index);
        Assert.Equal([1f, 2f], response.Data.Single().Embedding.Deserialize<float[]>());
        Assert.Equal(7, response.Usage.PromptTokens);
        Assert.Equal(7, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task OpenAINativeDimensionsSurviveAbsentStandardDimensions()
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal(64, body.GetProperty("dimensions").GetInt32());
            Assert.False(body.TryGetProperty("task", out _));
            Assert.False(body.TryGetProperty("overflow_strategy", out _));
            return JsonResponse(EmbeddingSuccess);
        });
        var request = EmbeddingRequest();
        request.AdditionalProperties = new() { ["dimensions"] = Element(64) };
        await provider.OpenAIEmbeddingRequestAsync(request);
    }

    [Theory]
    [InlineData(null, JsonValueKind.Array)]
    [InlineData("float", JsonValueKind.Array)]
    [InlineData("base64", JsonValueKind.String)]
    [InlineData("BASE64", JsonValueKind.String)]
    public async Task OpenAIEncodesFloatOrLittleEndianBase64Locally(string? format, JsonValueKind kind)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal(2, body.EnumerateObject().Count());
            return JsonResponse(EmbeddingSuccess);
        });
        var request = EmbeddingRequest();
        request.EncodingFormat = format;
        var embedding = (await provider.OpenAIEmbeddingRequestAsync(request)).Data.Single().Embedding;
        Assert.Equal(kind, embedding.ValueKind);
        if (kind == JsonValueKind.String)
            Assert.Equal("AACAPwAAAEA=", embedding.GetString());
    }

    [Fact]
    public async Task VercelMapsProviderOptionsAndPreservesOrderingHeadersAndNativeMetadata()
    {
        var provider = CreateProvider(async (message, token) =>
        {
            AssertTransport(message, "embeddings");
            var body = await ReadBody(message, token);
            Assert.Equal("kanon-2-embedder", body.GetProperty("model").GetString());
            Assert.Equal(["first", "second"], body.GetProperty("texts").Deserialize<string[]>());
            Assert.Equal(256, body.GetProperty("dimensions").GetInt32());
            Assert.Equal("retrieval/document", body.GetProperty("task").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("overflow_strategy").ValueKind);
            Assert.Equal(2, body.GetProperty("future_option").GetProperty("nested").GetArrayLength());
            Assert.False(body.TryGetProperty("unrelated", out _));
            Assert.False(body.TryGetProperty("providerOptions", out _));
            return JsonResponse("""
                {"embeddings":[{"index":1,"embedding":[3,4]},{"index":0,"embedding":[1,2]}],
                 "usage":{"input_tokens":13,"extra_usage":true},"extra":"metadata"}
                """);
        });
        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "isaacus/kanon-2-embedder", Values = ["first", "second"],
            ProviderOptions = new()
            {
                ["isaacus"] = Element(new
                {
                    model = "wrong", texts = new[] { "wrong" }, dimensions = 256,
                    task = "retrieval/document", overflow_strategy = (string?)null,
                    future_option = new { nested = new[] { 1, 2 } }
                }),
                ["other"] = Element(new { unrelated = true })
            }
        });
        Assert.Equal([1f, 2f], response.Embeddings.First());
        Assert.Equal([3f, 4f], response.Embeddings.Last());
        Assert.Equal(13, response.Usage!.Tokens);
        Assert.Equal("request-1", response.Response!.Headers!["x-request-id"]);
        Assert.Equal("metadata", response.ProviderMetadata!["isaacus"].GetProperty("extra").GetString());
        var usage = response.ProviderMetadata["isaacus"].GetProperty("usage");
        Assert.Equal(13, usage.GetProperty("input_tokens").GetInt32());
        Assert.True(usage.GetProperty("extra_usage").GetBoolean());
    }

    [Theory]
    [InlineData("isaacus/kanon-2-reranker", "kanon-2-reranker", null, 2)]
    [InlineData("kanon-universal-classifier", "kanon-universal-classifier", 1, 1)]
    public async Task RerankingMapsNativeOptionsAndResultsWithoutOverwritingAbsentTopN(
        string model, string expectedModel, int? topN, int expectedTopN)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            AssertTransport(message, "rerankings");
            var body = await ReadBody(message, token);
            Assert.Equal(expectedModel, body.GetProperty("model").GetString());
            Assert.Equal("legal query", body.GetProperty("query").GetString());
            Assert.Equal(["first", "second"], body.GetProperty("texts").Deserialize<string[]>());
            Assert.Equal(expectedTopN, body.GetProperty("top_n").GetInt32());
            Assert.True(body.GetProperty("is_iql").GetBoolean());
            Assert.Equal("chunk_avg", body.GetProperty("scoring_method").GetString());
            Assert.Equal(64, body.GetProperty("chunking_options").GetProperty("size").GetInt32());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("unrelated", out _));
            Assert.False(body.TryGetProperty("documents", out _));
            return JsonResponse(RerankingSuccess);
        });
        var request = RerankRequest(model);
        request.TopN = topN;
        request.ProviderOptions = new()
        {
            ["isaacus"] = Element(new
            {
                model = "wrong", query = "wrong", texts = new[] { "wrong" }, top_n = 2,
                is_iql = true, scoring_method = "chunk_avg", chunking_options = new { size = 64 },
                future_option = new { enabled = true }
            }),
            ["other"] = Element(new { unrelated = true })
        };
        var response = await provider.RerankingRequest(request);
        Assert.Equal([1, 0], response.Ranking.Select(item => item.Index));
        Assert.Equal([0.9f, 0.2f], response.Ranking.Select(item => item.RelevanceScore));
        Assert.Empty(response.Warnings);
        Assert.Equal($"isaacus/{expectedModel}", response.Response.ModelId);
        Assert.Equal("request-1", response.Response.Headers!["x-request-id"]);
        Assert.Equal("metadata", response.ProviderMetadata!["isaacus"].GetProperty("extra").GetString());
        Assert.Equal(11, response.ProviderMetadata["isaacus"].GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(11, ((JsonElement)response.Response.Body!).GetProperty("usage").GetProperty("input_tokens").GetInt32());
    }

    [Fact]
    public async Task RerankingDoesNotInjectOptionalNativeDefaults()
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal(["model", "query", "texts"], body.EnumerateObject().Select(property => property.Name));
            return JsonResponse(RerankingSuccess);
        });
        await provider.RerankingRequest(RerankRequest());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("[\" \"]")]
    [InlineData("[\"hello\",1]")]
    public async Task InvalidEmbeddingInputsAreRejectedBeforeTransport(string input)
    {
        var provider = CreateProvider(NoNetwork);
        var request = EmbeddingRequest();
        request.Input = JsonSerializer.Deserialize<JsonElement>(input);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[[1,2],[3,4]]")]
    public async Task TokenEmbeddingInputsAreExplicitlyUnsupported(string input)
    {
        var provider = CreateProvider(NoNetwork);
        var request = EmbeddingRequest();
        request.Input = JsonSerializer.Deserialize<JsonElement>(input);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(request));
    }

    [Fact]
    public async Task EmbeddingCommonFieldsAndUserAreValidated()
    {
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.OpenAIEmbeddingRequestAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.EmbeddingRequestAsync(null!));
        foreach (var model in new[] { "", " ", "isaacus/" })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(EmbeddingRequest(model)));
        var request = EmbeddingRequest();
        request.Dimensions = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        request.Dimensions = null;
        request.EncodingFormat = "int8";
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        request.EncodingFormat = null;
        request.User = "user";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "kanon-2-embedder", Values = []
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "kanon-2-embedder", Values = ["hello"],
            ProviderOptions = new() { ["isaacus"] = Element(new { user = "user" }) }
        }));
    }

    [Theory]
    [InlineData("object", "[{\"text\":\"hello\"}]")]
    [InlineData("image", "[\"hello\"]")]
    [InlineData("text", "[{\"text\":\"hello\"}]")]
    [InlineData("text", "[1]")]
    [InlineData("text", "[\"hello\",null]")]
    public async Task NonTextRerankingDocumentsAreNotStringified(string type, string values)
    {
        var provider = CreateProvider(NoNetwork);
        var request = RerankRequest();
        request.Documents = new() { Type = type, Values = JsonSerializer.Deserialize<JsonElement>(values) };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.RerankingRequest(request));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    [InlineData("[\" \"]")]
    public async Task EmptyOrInvalidRerankingDocumentsAreRejected(string values)
    {
        var provider = CreateProvider(NoNetwork);
        var request = RerankRequest();
        request.Documents = new() { Type = "text", Values = JsonSerializer.Deserialize<JsonElement>(values) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(request));
    }

    [Fact]
    public async Task RerankingCommonFieldsAreValidated()
    {
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.RerankingRequest(null!));
        foreach (var model in new[] { "", " ", "isaacus/" })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(RerankRequest(model)));
        var request = RerankRequest();
        request.Query = " ";
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(request));
        request.Query = "query";
        request.TopN = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(request));
        request.TopN = null;
        request.Documents = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.RerankingRequest(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingCredentialsFailBeforeNetwork(string? key)
    {
        var provider = CreateProvider(NoNetwork, key);
        var embedding = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAIEmbeddingRequestAsync(EmbeddingRequest()));
        var reranking = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RerankingRequest(RerankRequest()));
        Assert.Contains("No Isaacus API key", embedding.Message);
        Assert.Contains("No Isaacus API key", reranking.Message);
    }

    [Fact]
    public async Task ErrorsPreserveStatusAndNativeBodyOnBothEndpoints()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"detail":"rate limited"}""", HttpStatusCode.TooManyRequests)));
        var embedding = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAIEmbeddingRequestAsync(EmbeddingRequest()));
        var reranking = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RerankingRequest(RerankRequest()));
        foreach (var exception in new[] { embedding, reranking })
        {
            Assert.Contains("429", exception.Message);
            Assert.Contains("""{"detail":"rate limited"}""", exception.Message);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"embeddings\":[]}")]
    [InlineData("{\"embeddings\":[[1,2]]}")]
    [InlineData("{\"embeddings\":[{\"index\":0,\"embedding\":[]}]}")]
    [InlineData("{\"embeddings\":[{\"index\":1,\"embedding\":[1]}]}")]
    [InlineData("{\"embeddings\":[{\"index\":0,\"embedding\":[1e100]}]}")]
    [InlineData("{\"embeddings\":[{\"index\":0,\"embedding\":[\"bad\"]}]}")]
    [InlineData("{\"embeddings\":[{\"index\":0,\"embedding\":[1]}],\"usage\":{\"input_tokens\":\"bad\"}}")]
    public async Task InvalidEmbeddingSuccessIsRejected(string json)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(json)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAIEmbeddingRequestAsync(EmbeddingRequest()));
        Assert.Contains("invalid response", exception.Message);
    }

    [Fact]
    public async Task DuplicateEmbeddingIndicesAreRejected()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""
            {"embeddings":[{"index":0,"embedding":[1]},{"index":0,"embedding":[2]}],"usage":{"input_tokens":2}}
            """)));
        var request = EmbeddingRequest();
        request.Input = Element(new[] { "first", "second" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIEmbeddingRequestAsync(request));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"results\":{},\"usage\":{\"input_tokens\":1}}")]
    [InlineData("{\"results\":[{\"index\":0}],\"usage\":{\"input_tokens\":1}}")]
    [InlineData("{\"results\":[{\"index\":2,\"score\":0.5}],\"usage\":{\"input_tokens\":1}}")]
    [InlineData("{\"results\":[{\"index\":0,\"score\":\"bad\"}],\"usage\":{\"input_tokens\":1}}")]
    [InlineData("{\"results\":[{\"index\":0,\"score\":1e100}],\"usage\":{\"input_tokens\":1}}")]
    [InlineData("{\"results\":[{\"index\":0,\"score\":0.1},{\"index\":0,\"score\":0.2}],\"usage\":{\"input_tokens\":1}}")]
    [InlineData("{\"results\":[],\"usage\":{\"input_tokens\":-1}}")]
    public async Task InvalidRerankingSuccessIsRejected(string json)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(json)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RerankingRequest(RerankRequest()));
        Assert.Contains("invalid response", exception.Message);
    }

    [Fact]
    public async Task CancellationIsRespectedBeforeAndDuringTransport()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.OpenAIEmbeddingRequestAsync(EmbeddingRequest(), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "kanon-2-embedder", Values = ["hello"]
        }, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.RerankingRequest(RerankRequest(), source.Token));

        foreach (var reranking in new[] { false, true })
        {
            using var during = new CancellationTokenSource();
            provider = CreateProvider((_, token) =>
            {
                Assert.True(token.CanBeCanceled);
                during.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(JsonResponse(EmbeddingSuccess));
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reranking
                ? (Task)provider.RerankingRequest(RerankRequest(), during.Token)
                : provider.OpenAIEmbeddingRequestAsync(EmbeddingRequest(), during.Token));
        }
    }

    [Fact]
    public async Task HeaderRegistrationAndScopedProviderRegistryUseNativeBearerCredential()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Isaacus-Key"] = "  header-key  ";
        var snapshot = new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context });
        var resolver = new HeaderApiKeyResolver(snapshot);
        Assert.Equal("X-Isaacus-Key", HeaderApiKeyResolver.SupportedProviderHeaders["isaacus"]);
        Assert.Equal("header-key", resolver.Resolve("ISAACUS"));
        Assert.True(resolver.HasConfiguredKey("isaacus"));
        Assert.Equal("header-key", snapshot.ProviderKeys["isaacus"]);

        var services = new ServiceCollection();
        services.AddProviders();
        var descriptor = Assert.Single(services, service => service.ImplementationType == typeof(IsaacusProvider));
        Assert.Equal(typeof(IsaacusProvider), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new HttpClient(new Handler((message, _) =>
        {
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("header-key", message.Headers.Authorization.Parameter);
            return Task.FromResult(JsonResponse(EmbeddingSuccess));
        }))));
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("isaacus"));
        var provider = registry.GetModelProvider(scope.ServiceProvider, "isaacus");
        Assert.IsType<IsaacusProvider>(provider);
        Assert.Same(scope.ServiceProvider.GetRequiredService<IsaacusProvider>(), provider);
        await provider.OpenAIEmbeddingRequestAsync(EmbeddingRequest());
    }

    [Fact]
    public void EveryOtherSurfaceIsExplicitlyUnsupported()
    {
        var provider = CreateProvider(NoNetwork);
        var methods = typeof(IModelProvider).GetMethods().Where(method => method.Name is not
            ("GetIdentifier" or "ListModels" or "OpenAIEmbeddingRequestAsync" or "EmbeddingRequestAsync" or "RerankingRequest"));
        foreach (var method in methods)
        {
            var arguments = method.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None : null).ToArray();
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(provider, arguments));
            Assert.IsType<NotSupportedException>(exception.InnerException);
        }
    }

    private static void AssertTransport(HttpRequestMessage message, string endpoint)
    {
        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal($"https://api.isaacus.com/v1/{endpoint}", message.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", message.Headers.Authorization.Parameter);
        Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static OpenAIEmbeddingRequest EmbeddingRequest(string model = "isaacus/kanon-2-embedder")
        => new() { Model = model, Input = Element("hello") };
    private static RerankingRequest RerankRequest(string model = "isaacus/kanon-2-reranker")
        => new()
        {
            Model = model, Query = "legal query",
            Documents = new() { Type = "text", Values = Element(new[] { "first", "second" }) }
        };
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static IsaacusProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new HttpClient(new Handler(responder))));
    private static async Task<JsonElement> ReadBody(HttpRequestMessage request, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("x-request-id", "request-1");
        return response;
    }
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "isaacus" ? key : null;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
