using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Nomic;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Embeddings;

public sealed class NomicProviderEmbeddingTests
{
    private const string Success = """
        {"model":"nomic-embed-text-v1.5","embeddings":[[1.0,2.0]],"usage":{"prompt_tokens":3,"total_tokens":5}}
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-key")]
    public async Task DiscoveryUsesDocumentedStaticTextCatalogWithoutNetwork(string? key)
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network request."), key);
        var models = (await provider.ListModels()).ToArray();

        Assert.Equal("nomic", provider.GetIdentifier());
        Assert.Equal(["nomic/nomic-embed-text-v1.5", "nomic/nomic-embed-text-v1"],
            models.Select(model => model.Id));
        Assert.All(models, model =>
        {
            Assert.Equal("embedding", model.Type);
            Assert.Equal("Nomic", model.OwnedBy);
            Assert.Equal("model", model.Object);
            Assert.Equal(model.Id["nomic/".Length..], model.Name);
            Assert.Null(model.Pricing);
        });
    }

    [Theory]
    [InlineData("nomic/nomic-embed-text-v1.5", "nomic-embed-text-v1.5")]
    [InlineData("nomic-embed-text-v1.5", "nomic-embed-text-v1.5")]
    [InlineData("nomic/namespace/full-model", "namespace/full-model")]
    [InlineData("namespace/full-model", "namespace/full-model")]
    public async Task OpenAIMapsTextDimensionsAndPreservesFullNativeModel(string model, string nativeModel)
    {
        JsonElement body = default;
        var provider = CreateProvider(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api-atlas.nomic.ai/v1/embedding/text", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            body = await ReadBody(request, token);
            return JsonResponse(Success);
        });
        var request = Request(model);
        request.Dimensions = 256;
        request.AdditionalProperties = new()
        {
            ["task_type"] = Element("search_query"),
            ["long_text_mode"] = Element("truncate"),
            ["future_option"] = Element(new { nested = new[] { 1, 2 } })
        };
        var response = await provider.OpenAIEmbeddingRequestAsync(request);

        Assert.Equal(nativeModel, body.GetProperty("model").GetString());
        Assert.Equal(["hello"], body.GetProperty("texts").EnumerateArray().Select(text => text.GetString()));
        Assert.Equal(256, body.GetProperty("dimensionality").GetInt32());
        Assert.Equal("search_query", body.GetProperty("task_type").GetString());
        Assert.Equal("truncate", body.GetProperty("long_text_mode").GetString());
        Assert.Equal(2, body.GetProperty("future_option").GetProperty("nested").GetArrayLength());
        Assert.False(body.TryGetProperty("dimensions", out _));
        Assert.False(body.TryGetProperty("encoding_format", out _));
        Assert.False(body.TryGetProperty("input", out _));
        Assert.Equal("nomic/nomic-embed-text-v1.5", response.Model);
        Assert.Equal(3, response.Usage.PromptTokens);
        Assert.Equal(5, response.Usage.TotalTokens);
        Assert.Equal("list", response.Object);
        Assert.Equal("embedding", response.Data.Single().Object);
        Assert.Equal([1f, 2f], response.Data.Single().Embedding.Deserialize<float[]>()!);
    }

    [Fact]
    public async Task VercelPreservesNativeOptionsOrderingHeadersAndMetadata()
    {
        var provider = CreateProvider(async (request, token) =>
        {
            var body = await ReadBody(request, token);
            Assert.Equal(["first", "second"], body.GetProperty("texts").EnumerateArray().Select(text => text.GetString()));
            Assert.Equal(128, body.GetProperty("dimensionality").GetInt32());
            Assert.Equal("search_document", body.GetProperty("task_type").GetString());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("long_text_mode", out _));
            Assert.False(body.TryGetProperty("providerOptions", out _));
            Assert.False(body.TryGetProperty("unrelated", out _));
            return JsonResponse("""
                {"embeddings":[[1,2],[3,4]],"usage":{"prompt_tokens":7,"total_tokens":9},"extra":"metadata"}
                """);
        });
        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "nomic/nomic-embed-text-v1.5",
            Values = ["first", "second"],
            ProviderOptions = new()
            {
                ["nomic"] = Element(new { dimensionality = 128, task_type = "search_document", future_option = new { enabled = true } }),
                ["other"] = Element(new { unrelated = true })
            }
        });
        Assert.Equal([1f, 2f], response.Embeddings.First());
        Assert.Equal([3f, 4f], response.Embeddings.Last());
        Assert.Equal(7, response.Usage!.Tokens);
        Assert.Equal("request-1", response.Response!.Headers!["x-request-id"]);
        Assert.Equal("metadata", response.ProviderMetadata!["nomic"].GetProperty("extra").GetString());
        Assert.Equal(9, response.ProviderMetadata["nomic"].GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task VercelDimensionsAliasMapsWithoutAddingNativeDefaults()
    {
        var provider = CreateProvider(async (request, token) =>
        {
            var body = await ReadBody(request, token);
            Assert.Equal(64, body.GetProperty("dimensionality").GetInt32());
            Assert.False(body.TryGetProperty("dimensions", out _));
            Assert.False(body.TryGetProperty("task_type", out _));
            Assert.False(body.TryGetProperty("long_text_mode", out _));
            return JsonResponse(Success);
        });
        await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "nomic-embed-text-v1.5", Values = ["hello"],
            ProviderOptions = new() { ["nomic"] = Element(new { dimensions = 64 }) }
        });
    }

    [Theory]
    [InlineData(null, JsonValueKind.Array)]
    [InlineData("float", JsonValueKind.Array)]
    [InlineData("base64", JsonValueKind.String)]
    [InlineData("BASE64", JsonValueKind.String)]
    public async Task OpenAIEncodingIsAppliedLocally(string? encoding, JsonValueKind expectedKind)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.False(body.TryGetProperty("encoding_format", out _));
            Assert.False(body.TryGetProperty("task_type", out _));
            Assert.False(body.TryGetProperty("long_text_mode", out _));
            return JsonResponse(Success);
        });
        var request = Request();
        request.EncodingFormat = encoding;
        var embedding = (await provider.OpenAIEmbeddingRequestAsync(request)).Data.Single().Embedding;
        Assert.Equal(expectedKind, embedding.ValueKind);
        if (expectedKind == JsonValueKind.String)
            Assert.Equal("AACAPwAAAEA=", embedding.GetString());
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[[1,2],[3,4]]")]
    public async Task TokenInputsAreExplicitlyUnsupported(string input)
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network request."));
        var request = Request();
        request.Input = JsonDocument.Parse(input).RootElement.Clone();
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(request));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("[\"hello\",1]")]
    [InlineData("[\" \"]")]
    public async Task InvalidInputsAreRejectedBeforeNetwork(string input)
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network request."));
        var request = Request();
        request.Input = JsonDocument.Parse(input).RootElement.Clone();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
    }

    [Fact]
    public async Task InvalidCommonFieldsAndUnsupportedUserAreRejected()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network request."));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.OpenAIEmbeddingRequestAsync(null!));
        foreach (var model in new[] { "", " ", "nomic/" })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(Request(model)));
        var request = Request();
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
            Model = "nomic-embed-text-v1.5", Values = []
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeyFailsBeforeNetwork(string? key)
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network request."), key);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIEmbeddingRequestAsync(Request()));
        Assert.Contains("No Nomic API key", exception.Message);
    }

    [Fact]
    public async Task NativeErrorIncludesStatusAndBody()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"detail":"rate limited"}""", HttpStatusCode.TooManyRequests)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIEmbeddingRequestAsync(Request()));
        Assert.Contains("429", exception.Message);
        Assert.Contains("rate limited", exception.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"embeddings\":[]}")]
    [InlineData("{\"embeddings\":[[]]}")]
    [InlineData("{\"embeddings\":[[\"bad\"]]}")]
    [InlineData("{\"embeddings\":[[1e100]]}")]
    [InlineData("{\"embeddings\":[\"AACAPw==\"]}")]
    [InlineData("{\"embeddings\":[[1]],\"usage\":{\"prompt_tokens\":\"bad\"}}")]
    public async Task InvalidNativeSuccessIsRejected(string json)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(json)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIEmbeddingRequestAsync(Request()));
        Assert.Contains("invalid response", exception.Message);
    }

    [Fact]
    public async Task ModelFallbackDoesNotDoublePrefixOrTruncate()
    {
        foreach (var native in new[] { "{}", """{"model":"nomic/namespace/model"}""" })
        {
            var json = native == "{}" ? """{"embeddings":[[1]]}""" : """{"model":"nomic/namespace/model","embeddings":[[1]]}""";
            var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(json)));
            var result = await provider.OpenAIEmbeddingRequestAsync(Request("nomic/namespace/model"));
            Assert.Equal("nomic/namespace/model", result.Model);
            Assert.Equal(0, result.Usage.TotalTokens);
        }
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeAndDuringTransport()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network request."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAIEmbeddingRequestAsync(Request(), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(source.Token));

        using var during = new CancellationTokenSource();
        provider = CreateProvider((_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            during.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse(Success));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAIEmbeddingRequestAsync(Request(), during.Token));
    }

    [Fact]
    public void RegistrationAndHeaderAuthResolveNomic()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        var descriptor = Assert.Single(services, service => service.ImplementationType == typeof(NomicProvider));
        Assert.Equal(typeof(NomicProvider), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Nomic-Key"] = "  header-key  ";
        var snapshot = new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context });
        var resolver = new HeaderApiKeyResolver(snapshot);
        Assert.Equal("X-Nomic-Key", HeaderApiKeyResolver.SupportedProviderHeaders["nomic"]);
        Assert.Equal("header-key", resolver.Resolve("NOMIC"));
        Assert.Equal("header-key", snapshot.ProviderKeys["nomic"]);
        Assert.True(resolver.HasConfiguredKey("nomic"));
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new HttpClient(new Handler((_, _) =>
            throw new InvalidOperationException("Unexpected network request.")))));
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("nomic"));
        var registered = registry.GetModelProvider(scope.ServiceProvider, "nomic");
        Assert.IsType<NomicProvider>(registered);
        Assert.Same(scope.ServiceProvider.GetRequiredService<NomicProvider>(), registered);
    }

    [Fact]
    public async Task HeaderCredentialIsSentAsNativeBearer()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Nomic-Key"] = "header-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("header-key", request.Headers.Authorization!.Parameter);
            return Task.FromResult(JsonResponse(Success));
        }));
        await new NomicProvider(resolver, new Factory(client)).OpenAIEmbeddingRequestAsync(Request());
    }

    [Fact]
    public void EveryNonEmbeddingSurfaceIsExplicitlyUnsupported()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network request."));
        var methods = typeof(IModelProvider).GetMethods().Where(method =>
            method.Name is not ("GetIdentifier" or "ListModels" or "OpenAIEmbeddingRequestAsync" or "EmbeddingRequestAsync"));
        foreach (var method in methods)
        {
            var arguments = method.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None : null).ToArray();
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(provider, arguments));
            Assert.IsType<NotSupportedException>(exception.InnerException);
        }
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static OpenAIEmbeddingRequest Request(string model = "nomic/nomic-embed-text-v1.5")
        => new() { Model = model, Input = Element("hello") };

    private static NomicProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new HttpClient(new Handler(responder))));

    private static async Task<JsonElement> ReadBody(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("x-request-id", "request-1");
        return response;
    }

    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "nomic" ? key : null;
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
