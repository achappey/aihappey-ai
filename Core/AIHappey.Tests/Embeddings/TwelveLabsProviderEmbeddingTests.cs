using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.TwelveLabs;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Embeddings;

public sealed class TwelveLabsProviderEmbeddingTests
{
    private const string Success = """
        {"data":[{"embedding":[1,2],"embedding_uncertainty":[0.1,0.2]}],
         "usage":{"input_tokens":{"text":3,"image":7},"truncated":true,"truncation_reason":"model_context_window"},
         "metadata":{"future":"preserved"}}
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-key")]
    public async Task DiscoveryUsesOfficialStaticModelsWithoutNetwork(string? key)
    {
        var provider = CreateProvider(NoNetwork, key);
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal("twelvelabs", provider.GetIdentifier());
        Assert.Equal(["twelvelabs/marengo3.5", "twelvelabs/marengo3.0"], models.Select(model => model.Id));
        Assert.All(models, model =>
        {
            Assert.Equal("embedding", model.Type);
            Assert.Equal("TwelveLabs", model.OwnedBy);
            Assert.Equal("model", model.Object);
            Assert.Equal(model.Id["twelvelabs/".Length..], model.Name);
            Assert.Null(model.Pricing);
        });
    }

    [Theory]
    [InlineData("twelvelabs/marengo3.5", "marengo3.5", "multi_input")]
    [InlineData("marengo3.5", "marengo3.5", "multi_input")]
    [InlineData("TWELVELABS/marengo3.0", "marengo3.0", "text")]
    [InlineData("marengo3.0", "marengo3.0", "text")]
    public async Task MapsModelsTextAndDimensionsWithCanonicalPrecedenceWithoutMutation(
        string model, string nativeModel, string inputType)
    {
        var nested = Element(new { input_text = "wrong", media_sources = new[] { new { name = "image", url = "https://example.com/image.jpg" } } });
        var request = Request(model);
        request.Dimensions = nativeModel == "marengo3.5" ? 256 : null;
        request.AdditionalProperties = new()
        {
            ["model_name"] = Element("wrong"),
            ["input_type"] = Element("video"),
            [inputType] = nested,
            ["auto_truncate"] = Element(true),
            ["embedding_uncertainty"] = Element(true),
            ["future_option"] = Element(new { enabled = true })
        };
        if (nativeModel == "marengo3.5")
            request.AdditionalProperties["embedding_dimension"] = Element(64);
        var before = JsonSerializer.Serialize(request);
        var provider = CreateProvider(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.twelvelabs.io/v1.3/embed-v2", message.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("x-api-key")));
            Assert.Null(message.Headers.Authorization);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            var body = await ReadBody(message, token);
            Assert.Equal(nativeModel, body.GetProperty("model_name").GetString());
            Assert.Equal(inputType, body.GetProperty("input_type").GetString());
            Assert.Equal("hello", body.GetProperty(inputType).GetProperty("input_text").GetString());
            Assert.Equal(nested.GetProperty("media_sources").GetRawText(),
                body.GetProperty(inputType).GetProperty("media_sources").GetRawText());
            if (nativeModel == "marengo3.5")
                Assert.Equal(256, body.GetProperty("embedding_dimension").GetInt32());
            else
                Assert.False(body.TryGetProperty("embedding_dimension", out _));
            Assert.True(body.GetProperty("auto_truncate").GetBoolean());
            Assert.True(body.GetProperty("embedding_uncertainty").GetBoolean());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            foreach (var field in new[] { "model", "input", "dimensions", "encoding_format", "user" })
                Assert.False(body.TryGetProperty(field, out _));
            return JsonResponse(Success);
        });
        var response = await provider.OpenAIEmbeddingRequestAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("wrong", nested.GetProperty("input_text").GetString());
        Assert.Equal($"twelvelabs/{nativeModel}", response.Model);
        Assert.Equal(10, response.Usage.PromptTokens);
        Assert.Equal(10, response.Usage.TotalTokens);
        Assert.Equal("list", response.Object);
        Assert.Equal("embedding", response.Data.Single().Object);
        Assert.Equal([1f, 2f], response.Data.Single().Embedding.Deserialize<float[]>()!);
    }

    [Fact]
    public async Task VercelBatchPreservesOrderingOptionsUsageAndAllNativeResponses()
    {
        var sent = new List<string>();
        var options = Element(new
        {
            dimensions = 128, model_name = "wrong", input_type = "video",
            auto_truncate = true, embedding_uncertainty = true,
            multi_input = new { input_text = "wrong", media_sources = new[] { new { name = "pic", url = "https://example.com/pic" } } }
        });
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            sent.Add(body.GetProperty("multi_input").GetProperty("input_text").GetString()!);
            Assert.Equal("marengo3.5", body.GetProperty("model_name").GetString());
            Assert.Equal("multi_input", body.GetProperty("input_type").GetString());
            Assert.Equal(128, body.GetProperty("embedding_dimension").GetInt32());
            Assert.Equal(1, body.GetProperty("multi_input").GetProperty("media_sources").GetArrayLength());
            Assert.True(body.GetProperty("auto_truncate").GetBoolean());
            Assert.True(body.GetProperty("embedding_uncertainty").GetBoolean());
            Assert.False(body.TryGetProperty("unrelated", out _));
            return JsonResponse(sent.Count == 1 ? Success : """
                {"data":[{"embedding":[3,4]}],"usage":{"input_tokens":{"text":2,"audio":5,"future_modality":1},"truncated":false}}
                """);
        });
        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "twelvelabs/marengo3.5", Values = ["first", "second"],
            ProviderOptions = new() { ["twelvelabs"] = options, ["other"] = Element(new { unrelated = true }) }
        });
        Assert.Equal(["first", "second"], sent);
        Assert.Equal([1f, 2f], response.Embeddings.First());
        Assert.Equal([3f, 4f], response.Embeddings.Last());
        Assert.Equal(18, response.Usage!.Tokens);
        Assert.Equal("request-1", response.Response!.Headers!["x-request-id"]);
        var native = Assert.IsType<JsonElement>(response.Response.Body);
        var results = native.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.True(results[0].GetProperty("usage").GetProperty("truncated").GetBoolean());
        Assert.Equal("model_context_window", results[0].GetProperty("usage").GetProperty("truncation_reason").GetString());
        Assert.Equal(2, results[0].GetProperty("data")[0].GetProperty("embedding_uncertainty").GetArrayLength());
        Assert.Equal("preserved", results[0].GetProperty("metadata").GetProperty("future").GetString());
        Assert.Equal(native.GetRawText(), response.ProviderMetadata!["twelvelabs"].GetRawText());
        Assert.Equal("wrong", options.GetProperty("multi_input").GetProperty("input_text").GetString());
    }

    [Fact]
    public async Task NativeDimensionsPassThroughAndDefaultsRemainNative()
    {
        var count = 0;
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            if (count++ == 0)
                Assert.Equal(256, body.GetProperty("embedding_dimension").GetInt32());
            else
                Assert.False(body.TryGetProperty("embedding_dimension", out _));
            Assert.False(body.TryGetProperty("auto_truncate", out _));
            Assert.False(body.TryGetProperty("embedding_uncertainty", out _));
            return JsonResponse("""{"data":[{"embedding":[1]}]}""");
        });
        await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "marengo3.5", Values = ["hello"],
            ProviderOptions = new() { ["twelvelabs"] = Element(new { embedding_dimension = 256 }) }
        });
        var openAI = await provider.OpenAIEmbeddingRequestAsync(Request("marengo3.0"));
        Assert.Null(openAI.Usage);
        Assert.Equal(JsonValueKind.Null, Element(openAI).GetProperty("usage").ValueKind);
        var vercel = await provider.EmbeddingRequestAsync(new EmbeddingRequest { Model = "marengo3.0", Values = ["hello"] });
        Assert.Null(vercel.Usage);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    public async Task Marengo35SupportsDocumentedDimensionsAcrossBothSurfaces(int dimensions)
    {
        var provider = CreateProvider(async (message, token) =>
        {
            var body = await ReadBody(message, token);
            Assert.Equal(dimensions, body.GetProperty("embedding_dimension").GetInt32());
            Assert.False(body.TryGetProperty("dimensions", out _));
            return JsonResponse(Success);
        });
        var request = Request();
        request.Dimensions = dimensions;
        await provider.OpenAIEmbeddingRequestAsync(request);
        request.Dimensions = null;
        request.AdditionalProperties = new() { ["embedding_dimension"] = Element(dimensions) };
        await provider.OpenAIEmbeddingRequestAsync(request);
        foreach (var field in new[] { "dimensions", "embedding_dimension" })
            await provider.EmbeddingRequestAsync(new EmbeddingRequest
            {
                Model = "twelvelabs/marengo3.5", Values = ["hello"],
                ProviderOptions = new() { ["twelvelabs"] = Element(new Dictionary<string, int> { [field] = dimensions }) }
            });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(129)]
    [InlineData(1024)]
    public async Task Marengo35RejectsUnsupportedDimensionsBeforeNetwork(int dimensions)
    {
        var provider = CreateProvider(NoNetwork);
        var request = Request();
        request.Dimensions = dimensions;
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        request.Dimensions = null;
        request.AdditionalProperties = new() { ["embedding_dimension"] = Element(dimensions) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        foreach (var field in new[] { "dimensions", "embedding_dimension" })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
            {
                Model = "marengo3.5", Values = ["hello"],
                ProviderOptions = new() { ["twelvelabs"] = Element(new Dictionary<string, int> { [field] = dimensions }) }
            }));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    public async Task Marengo30RejectsDimensionsAcrossBothSurfacesBeforeNetwork(int dimensions)
    {
        var provider = CreateProvider(NoNetwork);
        var request = Request("twelvelabs/marengo3.0");
        request.Dimensions = dimensions;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        request.Dimensions = null;
        request.AdditionalProperties = new() { ["embedding_dimension"] = Element(dimensions) };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        foreach (var field in new[] { "dimensions", "embedding_dimension" })
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
            {
                Model = "marengo3.0", Values = ["hello"],
                ProviderOptions = new() { ["twelvelabs"] = Element(new Dictionary<string, int> { [field] = dimensions }) }
            }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"256\"")]
    [InlineData("128.5")]
    [InlineData("true")]
    [InlineData("2147483648")]
    public async Task InvalidNativeDimensionsAreRejectedBeforeNetwork(string json)
    {
        var provider = CreateProvider(NoNetwork);
        var request = Request();
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        request.AdditionalProperties = new() { ["embedding_dimension"] = value };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "marengo3.5", Values = ["hello"],
            ProviderOptions = new() { ["twelvelabs"] = Element(new { embedding_dimension = value }) }
        }));
    }

    [Fact]
    public async Task Marengo35OmitsDimensionsWhenUnspecified()
    {
        await CreateProvider(async (message, token) =>
        {
            Assert.False((await ReadBody(message, token)).TryGetProperty("embedding_dimension", out _));
            return JsonResponse(Success);
        }).OpenAIEmbeddingRequestAsync(Request());
    }

    [Fact]
    public async Task MissingUsageForAnyBatchItemDoesNotFabricateAggregate()
    {
        var count = 0;
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(count++ == 0
            ? Success : """{"data":[{"embedding":[1,2]}],"usage":null}""")));
        var request = Request();
        request.Input = Element(new[] { "first", "second" });
        var result = await provider.OpenAIEmbeddingRequestAsync(request);
        Assert.Equal([0, 1], result.Data.Select(item => item.Index));
        Assert.Null(result.Usage);
    }

    [Theory]
    [InlineData(null, JsonValueKind.Array)]
    [InlineData("float", JsonValueKind.Array)]
    [InlineData("base64", JsonValueKind.String)]
    [InlineData("BASE64", JsonValueKind.String)]
    public async Task EncodingAppliedLocally(string? encoding, JsonValueKind kind)
    {
        var request = Request();
        request.EncodingFormat = encoding;
        var response = await CreateProvider((_, _) => Task.FromResult(JsonResponse(Success))).OpenAIEmbeddingRequestAsync(request);
        Assert.Equal(kind, response.Data.Single().Embedding.ValueKind);
        if (kind == JsonValueKind.String)
            Assert.Equal("AACAPwAAAEA=", response.Data.Single().Embedding.GetString());
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[[1,2],[3,4]]")]
    public async Task TokenIdsUnsupported(string input)
    {
        var request = Request();
        request.Input = JsonDocument.Parse(input).RootElement.Clone();
        await Assert.ThrowsAsync<NotSupportedException>(() => CreateProvider(NoNetwork).OpenAIEmbeddingRequestAsync(request));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("[\" \"]")]
    [InlineData("[\"hello\",1]")]
    public async Task InvalidInputsFailBeforeNetwork(string input)
    {
        var request = Request();
        request.Input = JsonDocument.Parse(input).RootElement.Clone();
        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(NoNetwork).OpenAIEmbeddingRequestAsync(request));
    }

    [Fact]
    public async Task InvalidStandardFieldsAndNativeInputAreRejected()
    {
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.OpenAIEmbeddingRequestAsync(null!));
        foreach (var model in new[] { "", " ", "twelvelabs/" })
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
        request.User = null;
        request.AdditionalProperties = new() { ["multi_input"] = Element("bad") };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(request));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest { Model = "marengo3.5" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "marengo3.5", Values = ["hello"],
            ProviderOptions = new() { ["twelvelabs"] = Element(new { user = "user" }) }
        }));
    }

    [Theory]
    [InlineData("twelvelabs/marengo3.6")]
    [InlineData("marengo3.6")]
    [InlineData("namespace/marengo3.5")]
    public async Task UndocumentedModelsAreUnsupportedBeforeNetwork(string model)
    {
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(Request(model)));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = model, Values = ["hello"]
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeyFailsBeforeNetwork(string? key)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(NoNetwork, key).OpenAIEmbeddingRequestAsync(Request()));
        Assert.Contains("No TwelveLabs API key", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NativeErrorsPreserveTypedStatusAndBody(HttpStatusCode status)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""{"message":"native failure"}""", status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.OpenAIEmbeddingRequestAsync(Request()));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains(((int)status).ToString(), error.Message);
        Assert.Contains("native failure", error.Message);
        error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "marengo3.5", Values = ["hello"]
        }));
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{}]}")]
    [InlineData("{\"data\":[{\"embedding\":[]}]}")]
    [InlineData("{\"data\":[{\"embedding\":[1]},{\"embedding\":[2]}]}")]
    [InlineData("{\"data\":[{\"embedding\":[\"bad\"]}]}")]
    [InlineData("{\"data\":[{\"embedding\":[1e100]}]}")]
    [InlineData("{\"data\":[{\"embedding\":\"AACAPw==\"}]}")]
    [InlineData("{\"data\":[{\"embedding\":[1]}],\"usage\":{}}")]
    [InlineData("{\"data\":[{\"embedding\":[1]}],\"usage\":{\"input_tokens\":{\"text\":\"bad\"}}}")]
    [InlineData("{\"data\":[{\"embedding\":[1]}],\"usage\":{\"input_tokens\":{\"text\":-1}}}")]
    [InlineData("{\"data\":[{\"embedding\":[1]}],\"usage\":{\"input_tokens\":{\"text\":2147483647,\"image\":1}}}")]
    public async Task InvalidResponsesAreRejected(string json)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(json)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIEmbeddingRequestAsync(Request()));
        Assert.Contains("invalid response", error.Message);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAndDuringTransportAndBetweenBatchItems()
    {
        using var before = new CancellationTokenSource();
        before.Cancel();
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAIEmbeddingRequestAsync(Request(), before.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(before.Token));
        using var during = new CancellationTokenSource();
        provider = CreateProvider((_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            during.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse(Success));
        });
        var request = Request();
        request.Input = Element(new[] { "first", "second" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAIEmbeddingRequestAsync(request, during.Token));
    }

    [Fact]
    public async Task ScopedRegistrationHeaderAuthAndPerRequestCredentials()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        var descriptor = Assert.Single(services, service => service.ImplementationType == typeof(TwelveLabsProvider));
        Assert.Equal(typeof(TwelveLabsProvider), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-TwelveLabs-Key"] = "  header-key  ";
        var snapshot = new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context });
        var resolver = new HeaderApiKeyResolver(snapshot);
        Assert.Equal("X-TwelveLabs-Key", HeaderApiKeyResolver.SupportedProviderHeaders["twelvelabs"]);
        Assert.Equal("header-key", resolver.Resolve("TWELVELABS"));
        Assert.True(resolver.HasConfiguredKey("twelvelabs"));
        Assert.Equal("header-key", snapshot.ProviderKeys["twelvelabs"]);
        using var client = new HttpClient(new Handler((message, _) =>
        {
            Assert.Equal("header-key", Assert.Single(message.Headers.GetValues("x-api-key")));
            return Task.FromResult(JsonResponse(Success));
        }));
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(client));
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("twelvelabs"));
        var provider = Assert.IsType<TwelveLabsProvider>(registry.GetModelProvider(scope.ServiceProvider, "twelvelabs"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<TwelveLabsProvider>(), provider);
        await provider.OpenAIEmbeddingRequestAsync(Request());
        await provider.OpenAIEmbeddingRequestAsync(Request("marengo3.0"));
        Assert.False(client.DefaultRequestHeaders.Contains("x-api-key"));
    }
   

    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static OpenAIEmbeddingRequest Request(string model = "twelvelabs/marengo3.5")
        => new() { Model = model, Input = Element("hello") };
    private static TwelveLabsProvider CreateProvider(
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
        public string? Resolve(string provider) => provider == "twelvelabs" ? key : null;
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
