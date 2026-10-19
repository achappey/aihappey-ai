using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ContextualAI;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.ContextualAI;

public sealed class ContextualAIProviderTests
{
    private const string Success = """
        {"results":[{"index":1,"relevance_score":0.9},{"index":0,"relevance_score":0.2}],"extra":"native"}
        """;

    [Fact]
    public async Task DiscoveryListsOfficialRerankModelsWithoutCredentialsOrNetwork()
    {
        var models = (await CreateProvider(NoNetwork, null).ListModels()).ToArray();
        Assert.Equal([
            "contextualai/ctxl-rerank-v2-instruct-multilingual",
            "contextualai/ctxl-rerank-v2-instruct-multilingual-mini",
            "contextualai/ctxl-rerank-v1-instruct"
        ], models.Select(model => model.Id));
        Assert.All(models, model =>
        {
            Assert.Equal("reranking", model.Type);
            Assert.Equal("Contextual AI", model.OwnedBy);
            Assert.Equal(model.Id["contextualai/".Length..], model.Name);
            Assert.Null(model.Pricing);
            Assert.Null(model.ContextWindow);
            Assert.Null(model.Created);
        });
    }

    [Theory]
    [InlineData("contextualai/ctxl-rerank-v2-instruct-multilingual", "ctxl-rerank-v2-instruct-multilingual")]
    [InlineData("ctxl-rerank-v1-instruct", "ctxl-rerank-v1-instruct")]
    [InlineData("CONTEXTUALAI/namespace/model", "namespace/model")]
    public async Task RerankMapsStandardFieldsAndPreservesNativeOptionsAndResponse(string model, string nativeModel)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.contextual.ai/v1/rerank", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", message.Headers.Authorization.Parameter);
            var body = await ReadBody(message, token);
            Assert.Equal(nativeModel, body.GetProperty("model").GetString());
            Assert.Equal("query", body.GetProperty("query").GetString());
            Assert.Equal(["first", "second"], body.GetProperty("documents").Deserialize<string[]>()!);
            Assert.Equal(2, body.GetProperty("top_n").GetInt32());
            Assert.Equal("Prefer recent sources", body.GetProperty("instruction").GetString());
            Assert.Equal(["2026", ""], body.GetProperty("metadata").Deserialize<string[]>()!);
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("other_provider_option", out _));
            return JsonResponse(Success);
        });
        var request = Request(model);
        request.TopN = 2;
        request.ProviderOptions = new()
        {
            ["contextualai"] = Element(new
            {
                model = "wrong-model",
                query = "wrong-query",
                documents = new[] { "wrong-document" },
                top_n = 1,
                instruction = "Prefer recent sources",
                metadata = new[] { "2026", "" },
                future_option = new { enabled = true }
            }),
            ["other"] = Element(new { other_provider_option = true })
        };
        var originalOptions = request.ProviderOptions["contextualai"].GetRawText();
        var before = DateTime.UtcNow;
        var response = await provider.RerankingRequest(request);
        Assert.Equal([1, 0], response.Ranking.Select(item => item.Index));
        Assert.Equal([0.9f, 0.2f], response.Ranking.Select(item => item.RelevanceScore));
        Assert.Equal($"contextualai/{nativeModel}", response.Response.ModelId);
        Assert.InRange(response.Response.Timestamp, before, DateTime.UtcNow);
        Assert.Equal("request-1", response.Response.Headers!["x-request-id"]);
        var bodyResponse = Assert.IsType<JsonElement>(response.Response.Body);
        Assert.Equal("native", bodyResponse.GetProperty("extra").GetString());
        Assert.Equal("native", response.ProviderMetadata!["contextualai"].GetProperty("extra").GetString());
        Assert.Empty(response.Warnings);
        Assert.Equal(originalOptions, request.ProviderOptions["contextualai"].GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentStandardTopNRetainsNativeValueWithoutInjectingDefaults(bool nativeTopN)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal(nativeTopN, body.TryGetProperty("top_n", out var topN));
            if (nativeTopN)
                Assert.Equal(1, topN.GetInt32());
            Assert.False(body.TryGetProperty("instruction", out _));
            Assert.False(body.TryGetProperty("metadata", out _));
            return JsonResponse(Success);
        });
        var request = Request();
        if (nativeTopN)
            request.ProviderOptions = new() { ["contextualai"] = Element(new { top_n = 1 }) };
        await provider.RerankingRequest(request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeyFailsBeforeNetwork(string? key)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(NoNetwork, key).RerankingRequest(Request()));

    [Theory]
    [InlineData("object", "[{\"text\":\"first\"}]")]
    [InlineData("image", "[\"first\"]")]
    [InlineData("text", "[1]")]
    [InlineData("text", "[null]")]
    public async Task NonTextDocumentsAreRejected(string type, string values)
    {
        var request = Request();
        request.Documents = new() { Type = type, Values = JsonSerializer.Deserialize<JsonElement>(values) };
        await Assert.ThrowsAsync<NotSupportedException>(() => CreateProvider(NoNetwork).RerankingRequest(request));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task InvalidDocumentArraysAreRejected(string values)
    {
        var request = Request();
        request.Documents = new() { Type = "text", Values = JsonSerializer.Deserialize<JsonElement>(values) };
        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(NoNetwork).RerankingRequest(request));
    }

    [Fact]
    public async Task InvalidCommonFieldsAreRejected()
    {
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.RerankingRequest(null!));
        foreach (var model in new[] { "", " ", "contextualai/" })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(Request(model)));
        var request = Request();
        request.TopN = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(request));
        request.TopN = null;
        request.Query = " ";
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(request));
        request.Query = "query";
        request.Documents = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.RerankingRequest(request));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ErrorsPreserveStatusAndProviderBody(HttpStatusCode status)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""{"detail":"native error"}""", status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.RerankingRequest(Request()));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("native error", error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"results\":{}}")]
    [InlineData("{\"results\":[null]}")]
    [InlineData("{\"results\":[{\"index\":0}]}")]
    [InlineData("{\"results\":[{\"index\":2,\"relevance_score\":0.5}]}")]
    [InlineData("{\"results\":[{\"index\":-1,\"relevance_score\":0.5}]}")]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":\"bad\"}]}")]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":1e100}]}")]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":0.1},{\"index\":0,\"relevance_score\":0.2}]}")]
    public async Task MalformedSuccessResponsesFailExplicitly(string json)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(json)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RerankingRequest(Request()));
        Assert.Contains("invalid response", error.Message);
    }

    [Fact]
    public async Task CancellationIsRespectedBeforeAndDuringTransport()
    {
        using var before = new CancellationTokenSource();
        before.Cancel();
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(before.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RerankingRequest(Request(), before.Token));
        using var during = new CancellationTokenSource();
        provider = CreateProvider((_, token) =>
        {
            during.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse(Success));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RerankingRequest(Request(), during.Token));
    }

    [Fact]
    public async Task HeaderAndScopedRegistryRegistrationExecuteDiscoveredModels()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-ContextualAI-Key"] = "  test-key  ";
        var snapshot = new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context });
        var resolver = new HeaderApiKeyResolver(snapshot);
        Assert.Equal("X-ContextualAI-Key", HeaderApiKeyResolver.SupportedProviderHeaders["contextualai"]);
        Assert.Equal("test-key", resolver.Resolve("CONTEXTUALAI"));
        Assert.True(resolver.HasConfiguredKey("contextualai"));
        var services = new ServiceCollection();
        services.AddProviders();
        var descriptor = Assert.Single(services, item => item.ImplementationType == typeof(ContextualAIProvider));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(async (message, token) =>
        {
            Assert.Equal("test-key", message.Headers.Authorization!.Parameter);
            var body = await ReadBody(message, token);
            Assert.StartsWith("ctxl-rerank-", body.GetProperty("model").GetString());
            return JsonResponse(Success);
        })));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("contextualai"));
        var provider = Assert.IsType<ContextualAIProvider>(registry.GetModelProvider(scope.ServiceProvider, "contextualai"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<ContextualAIProvider>(), provider);
        foreach (var model in await provider.ListModels())
            await provider.RerankingRequest(Request(model.Id));
    }

    [Fact]
    public void OtherSurfacesAreExplicitlyUnsupported()
    {
        var provider = CreateProvider(NoNetwork);
        foreach (var method in typeof(IModelProvider).GetMethods()
                     .Where(method => method.Name is not ("GetIdentifier" or "ListModels" or "RerankingRequest")))
        {
            var args = method.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None : null).ToArray();
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(provider, args));
            Assert.IsType<NotSupportedException>(error.InnerException);
        }
    }

    private static RerankingRequest Request(string model = "contextualai/ctxl-rerank-v2-instruct-multilingual")
        => new()
        {
            Model = model, Query = "query",
            Documents = new() { Type = "text", Values = Element(new[] { "first", "second" }) }
        };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> ReadBody(HttpRequestMessage message, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static ContextualAIProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(responder)));
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("x-request-id", "request-1");
        return response;
    }
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "contextualai" ? key : null;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
