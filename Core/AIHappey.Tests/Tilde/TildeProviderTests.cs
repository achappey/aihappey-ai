using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Tilde;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Tilde;

public sealed class TildeProviderTests
{
    private const string EngineId = "a1234567-1234-1234-1234-123456789abc";
    private const string OtherId = "b1234567-1234-1234-1234-123456789abc";
    private const string Translation = """{"translations":[{"translation":"Sveiki"}],"detectedLanguage":"en","domain":"general"}""";
    private static string Catalog(params object[] engines) => JsonSerializer.Serialize(new { engines });
    private static object Engine(string id = EngineId, string vendor = "Tilde", string status = "Running")
        => new { id, name = "Native engine", vendor, status, sourceLanguages = new[] { "en", "de" },
            targetLanguages = new[] { "lv", "lt" }, domain = "general",
            domains = new { general = new { languages = new { en = new[] { "lv", "lt" } } } },
            supportsTermCollections = true, future = new { enabled = true } };

    [Fact]
    public async Task DiscoveryFiltersAndDeduplicatesNativeUsableEngines()
    {
        var provider = CreateProvider((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://translate.tilde.ai/api/engine", request.RequestUri!.AbsoluteUri);
            AssertAuth(request);
            return Task.FromResult(JsonResponse(Catalog(
                Engine(), Engine(), Engine(OtherId, "TildeOpen"),
                Engine(vendor: "DeepL"), Engine(vendor: "Google"), Engine(vendor: "Microsoft"),
                Engine(vendor: "OpenAi"), Engine(vendor: "ETranslation"), Engine(vendor: "Unknown"),
                Engine(status: "StandBy"), Engine(status: "Starting"), Engine(status: "Off"), Engine(status: "Error"),
                Engine(status: "Importing"), Engine(status: "Deleting"), Engine(status: "Unknown"),
                Engine(id: "invalid"), Engine(id: Guid.Empty.ToString()),
                new { id = OtherId, vendor = "Tilde", status = "Running", sourceLanguages = Array.Empty<string>(), targetLanguages = new[] { "lv" } })));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["tilde/" + EngineId, "tilde/" + OtherId], models.Select(m => m.Id));
        Assert.All(models, m =>
        {
            Assert.Equal("language", m.Type);
            Assert.Contains("translate", m.Tags!);
        });
        Assert.Equal("Native engine", models[0].Name);
        Assert.Equal("Tilde", models[0].OwnedBy);
        Assert.Equal("tilde/" + EngineId, Element(models[0]).GetProperty("id").GetString());
    }

    [Fact]
    public async Task DiscoveryCacheIsCredentialScopedAndAuthIsRequestScoped()
    {
        var key = new KeyResolver("first-key");
        var calls = 0;
        var factory = new Factory((request, _) =>
        {
            calls++;
            Assert.Equal(key.Key, Assert.Single(request.Headers.GetValues("x-api-key")));
            return Task.FromResult(JsonResponse(Catalog(Engine())));
        });
        var provider = new TildeProvider(key, factory, Cache());
        await provider.ListModels();
        await provider.ListModels();
        Assert.Equal(1, calls);
        key.Key = "second-key";
        await provider.ListModels();
        Assert.Equal(2, calls);
        Assert.False(factory.Client!.DefaultRequestHeaders.Contains("x-api-key"));
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
    [InlineData("{\"engines\":null}")]
    [InlineData("{\"engines\":{}}")]
    [InlineData("{")]
    public async Task MalformedCatalogHasNoFallback(string json)
        => await Assert.ThrowsAnyAsync<JsonException>(() =>
            CreateProvider((_, _) => Task.FromResult(JsonResponse(json))).ListModels());

    [Fact]
    public async Task EmptyCatalogRemainsEmpty()
        => Assert.Empty(await CreateProvider((_, _) => Task.FromResult(JsonResponse(Catalog()))).ListModels());

    [Theory]
    [InlineData("A1234567-1234-1234-1234-123456789ABC")]
    [InlineData("{a1234567-1234-1234-1234-123456789abc}")]
    [InlineData("a1234567123412341234123456789abc")]
    public async Task NativeEngineIdSpellingIsPreservedThroughDiscoveryAndTranslation(string nativeId)
    {
        var provider = CreateProvider(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(Catalog(Engine(nativeId)));
            var payload = await Body(request, token);
            Assert.Equal(nativeId, payload.GetProperty("engineId").GetString());
            return JsonResponse(Translation);
        });
        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("tilde/" + nativeId, model.Id);
        var options = Options();
        options.Model = model.Id;
        Assert.Equal(model.Id, (await provider.CompleteChatAsync(options)).Model);
    }

    [Theory]
    [InlineData("discovery", HttpStatusCode.Unauthorized)]
    [InlineData("chat", HttpStatusCode.TooManyRequests)]
    [InlineData("stream", HttpStatusCode.InternalServerError)]
    public async Task ErrorsRetainNativeStatusAndBodyAndAreNotCached(string operation, HttpStatusCode status)
    {
        var calls = 0;
        var provider = CreateProvider((_, _) => Task.FromResult(++calls == 1
            ? JsonResponse("""{"message":"native detail"}""", status)
            : JsonResponse(Catalog(Engine()))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Invoke(provider, operation));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("native detail", error.Message);
        Assert.Single(await provider.ListModels());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePayloadUsesUserTextAndSelectedEngineAndPreservesAdditionalProperties(bool prefixed)
    {
        var options = Options();
        options.Model = prefixed ? "tilde/" + EngineId : EngineId;
        options.Messages =
        [
            new() { Role = "system", Content = Element("Do not translate me") },
            new() { Role = "assistant", Content = Element("Previous translation") },
            new() { Role = "USER", Content = Element(new[] { new { type = "text", text = "First" }, new { type = "text", text = "Second" }, new { type = "text", text = " " } }) }
        ];
        options.AdditionalProperties = new()
        {
            ["srcLang"] = Element("en"), ["trgLang"] = Element("lv"),
            ["domain"] = Element("finance"), ["termCollections"] = Element(new[] { OtherId }),
            ["future"] = Element(new { enabled = true, values = new object[] { 1, "two" } }),
            ["text"] = Element(new[] { "wrong" }), ["engineId"] = Element(OtherId)
        };
        var original = JsonSerializer.Serialize(options.AdditionalProperties);
        var provider = CreateProvider(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://translate.tilde.ai/api/translate/text", request.RequestUri!.AbsoluteUri);
            AssertAuth(request);
            var payload = await Body(request, token);
            Assert.Equal(EngineId, payload.GetProperty("engineId").GetString());
            Assert.Equal(["First", "Second"], payload.GetProperty("text").EnumerateArray().Select(v => v.GetString()));
            foreach (var (name, value) in options.AdditionalProperties.Where(p => p.Key is not ("text" or "engineId")))
                Assert.Equal(value.GetRawText(), payload.GetProperty(name).GetRawText());
            return JsonResponse("""{"translations":[{"translation":"Pirmais"},{"translation":"Otrais"}],"domain":"finance","detectedLanguage":"en"}""");
        });
        var completion = await provider.CompleteChatAsync(options);
        Assert.Equal("Pirmais\nOtrais", Content(completion));
        Assert.Equal("tilde/" + EngineId, completion.Model);
        Assert.Null(completion.Usage);
        Assert.Equal("en", completion.AdditionalProperties!["tilde"].GetProperty("detectedLanguage").GetString());
        Assert.Equal("finance", completion.AdditionalProperties["tilde"].GetProperty("domain").GetString());
        Assert.Equal(original, JsonSerializer.Serialize(options.AdditionalProperties));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MultiTurnChatTranslatesOnlyLatestUserMessage(bool streaming, bool multipart)
    {
        var options = Options();
        options.Messages =
        [
            new() { Role = "user", Content = Element("Historical user text") },
            new() { Role = "assistant", Content = Element("Historical translation") },
            new() { Role = "USER", Content = multipart
                ? Element(new[] { new { type = "text", text = "First" }, new { type = "text", text = " " }, new { type = "text", text = "Second" } })
                : Element("Latest user text") },
            new() { Role = "assistant", Content = Element("Trailing assistant text") }
        ];
        var calls = 0;
        var provider = CreateProvider(async (request, token) =>
        {
            calls++;
            var payload = await Body(request, token);
            Assert.Equal(multipart ? new[] { "First", "Second" } : new[] { "Latest user text" },
                payload.GetProperty("text").EnumerateArray().Select(v => v.GetString()));
            return JsonResponse(multipart
                ? """{"translations":[{"translation":"Pirmais"},{"translation":"Otrais"}]}"""
                : Translation);
        });
        var expected = multipart ? "Pirmais\nOtrais" : "Sveiki";
        if (streaming)
        {
            var chunks = new List<ChatCompletionUpdate>();
            await foreach (var chunk in provider.CompleteChatStreamingAsync(options))
                chunks.Add(chunk);
            Assert.Equal(expected, Element(chunks[0]).GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
            Assert.Equal("stop", Element(chunks[^1]).GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        }
        else
            Assert.Equal(expected, Content(await provider.CompleteChatAsync(options)));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"   \"")]
    [InlineData("[]")]
    [InlineData("[{\"type\":\"text\",\"text\":\" \"}]")]
    public async Task EmptyLatestUserMessageDoesNotRetranslateHistory(string content)
    {
        var options = Options();
        options.Messages =
        [
            new() { Role = "user", Content = Element("Historical user text") },
            new() { Role = "assistant", Content = Element("Historical translation") },
            new() { Role = "user", Content = JsonSerializer.Deserialize<JsonElement>(content) }
        ];
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteChatAsync(options));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(options)) { }
        });
    }

    [Fact]
    public async Task RequiredTargetPayloadDoesNotInventOtherNativeDefaults()
    {
        var provider = CreateProvider(async (request, token) =>
        {
            var payload = await Body(request, token);
            Assert.Equal(["trgLang", "text", "engineId"], payload.EnumerateObject().Select(p => p.Name));
            Assert.Equal("lv", payload.GetProperty("trgLang").GetString());
            return JsonResponse(Translation);
        });
        Assert.Equal("Sveiki", Content(await provider.CompleteChatAsync(Options())));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task MissingOrInvalidNativeTargetIsRejectedWithoutNetworkOnBothChatPaths(string? targetJson)
    {
        var options = Options();
        options.AdditionalProperties = targetJson is null
            ? null : new() { ["trgLang"] = JsonSerializer.Deserialize<JsonElement>(targetJson) };
        var provider = CreateProvider(NoNetwork);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteChatAsync(options));
        Assert.Contains("trgLang", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(options)) { }
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"translations\":null}")]
    [InlineData("{\"translations\":[]}")]
    [InlineData("{\"translations\":[{},{}]}")]
    [InlineData("{\"translations\":[{}]}")]
    [InlineData("{\"translations\":[{\"translation\":null}]}")]
    [InlineData("{\"translations\":[{\"translation\":1}]}")]
    [InlineData("{\"translations\":[\"text\"]}")]
    public async Task InvalidTranslationCardinalityOrShapeThrows(string json)
        => await Assert.ThrowsAsync<JsonException>(() =>
            CreateProvider((_, _) => Task.FromResult(JsonResponse(json))).CompleteChatAsync(Options()));

    [Fact]
    public async Task EmptyNativeTranslationIsPreserved()
        => Assert.Equal("", Content(await CreateProvider((_, _) => Task.FromResult(
            JsonResponse("""{"translations":[{"translation":""}]}"""))).CompleteChatAsync(Options())));

    [Fact]
    public async Task StreamingBuffersBeforeFirstChunkAndMatchesNonStreamingWithoutInventingUsage()
    {
        var completed = false;
        var provider = CreateProvider((_, _) =>
        {
            completed = true;
            return Task.FromResult(JsonResponse(Translation));
        });
        var result = await provider.CompleteChatAsync(Options());
        completed = false;
        var chunks = new List<ChatCompletionUpdate>();
        await foreach (var chunk in provider.CompleteChatStreamingAsync(Options()))
        {
            Assert.True(completed);
            chunks.Add(chunk);
        }
        Assert.Equal(2, chunks.Count);
        Assert.Equal(Content(result), Element(chunks[0]).GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal("stop", Element(chunks[1]).GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(chunks[0].Id, chunks[1].Id);
        Assert.Equal(chunks[0].Created, chunks[1].Created);
        Assert.All(chunks, c => { Assert.Null(c.Usage); Assert.Equal("tilde/" + EngineId, c.Model); });
    }

    [Theory]
    [InlineData("discovery", false)]
    [InlineData("chat", false)]
    [InlineData("stream", false)]
    [InlineData("discovery", true)]
    [InlineData("chat", true)]
    [InlineData("stream", true)]
    public async Task CancellationPropagatesBeforeAndDuringNetwork(string operation, bool inflight)
    {
        using var source = new CancellationTokenSource();
        if (!inflight) source.Cancel();
        var provider = CreateProvider(async (_, token) =>
        {
            Assert.True(inflight);
            source.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(provider, operation, source.Token));
    }

    [Fact]
    public async Task CancellationBetweenBufferedChunksStopsEnumeration()
    {
        using var source = new CancellationTokenSource();
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(Translation)));
        await using var enumerator = provider.CompleteChatStreamingAsync(Options(), source.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("tilde/not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidModelIsRejected(string model)
    {
        var options = Options();
        options.Model = model;
        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(NoNetwork).CompleteChatAsync(options));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("tools")]
    [InlineData("tool-message")]
    [InlineData("tool-calls")]
    [InlineData("format")]
    [InlineData("no-user")]
    public async Task UnsupportedInputsAreRejectedRatherThanSilentlyLost(string kind)
    {
        var options = Options();
        switch (kind)
        {
            case "image": options.Messages = [new() { Role = "user", Content = Element(new[] { new { type = "image_url", image_url = new { url = "https://example.com/image.png" } } }) }]; break;
            case "tools": options.Tools = [new { type = "function" }]; break;
            case "tool-message": options.Messages = [new() { Role = "tool", Content = Element("output") }]; break;
            case "tool-calls": options.Messages = [new() { Role = "assistant", ToolCalls = [new { id = "call" }] }]; break;
            case "format": options.ResponseFormat = new { type = "json_object" }; break;
            default: options.Messages = [new() { Role = "system", Content = Element("Translate") }]; break;
        }
        var error = await Record.ExceptionAsync(() => CreateProvider(NoNetwork).CompleteChatAsync(options));
        Assert.NotNull(error);
        if (kind == "no-user") Assert.IsType<ArgumentException>(error);
        else Assert.IsType<NotSupportedException>(error);
    }

    [Fact]
    public async Task UnifiedResponsesMessagesAndStreamsUseStandardAdapters()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(Translation)));
        var request = new ResponseRequest
        {
            Model = "tilde/" + EngineId, Input = "Hi",
            Metadata = new() { ["tilde"] = Element(new { trgLang = "lv" }) }
        };
        var unifiedRequest = request.ToUnifiedRequest("tilde");
        Assert.Contains("Sveiki", JsonSerializer.Serialize(await provider.ExecuteUnifiedAsync(unifiedRequest)));
        Assert.Contains("Sveiki", JsonSerializer.Serialize(await provider.ResponsesAsync(request)));
        var messages = JsonSerializer.Deserialize<MessagesRequest>($$"""
            {"model":"tilde/{{EngineId}}","max_tokens":20,"messages":[{"role":"user","content":"Hi"}],
             "providerOptions":{"tilde":{"trgLang":"lv"} } }
            """, JsonSerializerOptions.Web)!;
        Assert.Contains("Sveiki", JsonSerializer.Serialize(await provider.MessagesAsync(messages, [])));
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(unifiedRequest)) events.Add(item);
        Assert.Contains(events, e => e.Event.Type == "text-delta");
        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(events, e => e.Event.Type == "finish").Event.Data);
        Assert.Null(finish.InputTokens);
        Assert.Null(finish.OutputTokens);
        Assert.Null(finish.TotalTokens);
        var responseParts = new List<object>();
        await foreach (var part in provider.ResponsesStreamingAsync(request)) responseParts.Add(part);
        Assert.Contains("Sveiki", JsonSerializer.Serialize(responseParts));
        var messageParts = new List<object>();
        await foreach (var part in provider.MessagesStreamingAsync(messages, [])) messageParts.Add(part);
        Assert.Contains("Sveiki", JsonSerializer.Serialize(messageParts));
        var ui = JsonSerializer.Deserialize<ChatRequest>($$"""
            {"id":"chat","model":"tilde/{{EngineId}}","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}],
             "providerMetadata":{"tilde":{"trgLang":"lv"} } }
            """, JsonSerializerOptions.Web)!;
        var uiParts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(ui)) uiParts.Add(part);
        Assert.Equal("Sveiki", string.Concat(uiParts.OfType<TextDeltaUIMessageStreamPart>().Select(p => p.Delta)));
    }

    [Theory]
    [InlineData("responses", false, "providerOptions")]
    [InlineData("responses", true, "providerOptions")]
    [InlineData("messages", false, "providerOptions")]
    [InlineData("messages", true, "providerOptions")]
    [InlineData("responses", false, "native")]
    [InlineData("responses", true, "native")]
    [InlineData("messages", false, "native")]
    [InlineData("messages", true, "native")]
    [InlineData("responses", false, "metadata")]
    [InlineData("responses", true, "metadata")]
    [InlineData("messages", false, "metadata")]
    [InlineData("messages", true, "metadata")]
    public async Task RawAdaptedRequestsForwardNativeOptionsWithoutReplacingStandardTextOrEngine(
        string surface, bool streaming, string container)
    {
        var native = Element(new
        {
            srcLang = "en", trgLang = "lv", domain = "finance", termCollections = new[] { OtherId },
            future = new { values = new object[] { 1, true, "native" } },
            engineId = OtherId, text = new[] { "wrong" }
        });
        var raw = new Dictionary<string, object?>
        {
            ["model"] = "tilde/" + EngineId, ["stream"] = streaming
        };
        if (surface == "responses")
            raw["input"] = "Hi";
        else
        {
            raw["max_tokens"] = 20;
            raw["messages"] = new[] { new { role = "user", content = "Hi" } };
        }
        if (container == "native")
            foreach (var property in native.EnumerateObject())
                raw[property.Name] = property.Value;
        else if (container == "metadata")
            raw["metadata"] = new { tilde = native };
        else
            raw["providerOptions"] = new { tilde = native, other = new { ignored = true } };
        var json = JsonSerializer.Serialize(raw);
        var calls = 0;
        var provider = CreateProvider(async (request, token) =>
        {
            calls++;
            var payload = await Body(request, token);
            foreach (var property in native.EnumerateObject().Where(p => p.Name is not ("text" or "engineId")))
                Assert.Equal(property.Value.GetRawText(), payload.GetProperty(property.Name).GetRawText());
            Assert.Equal(EngineId, payload.GetProperty("engineId").GetString());
            Assert.Equal("Hi", Assert.Single(payload.GetProperty("text").EnumerateArray()).GetString());
            Assert.False(payload.TryGetProperty("providerOptions", out _));
            Assert.False(payload.TryGetProperty("tilde", out _));
            Assert.False(payload.TryGetProperty("other", out _));
            return JsonResponse(Translation);
        });
        if (surface == "responses")
        {
            var request = JsonSerializer.Deserialize<ResponseRequest>(json, JsonSerializerOptions.Web)!;
            var original = JsonSerializer.Serialize(request);
            if (streaming)
                await foreach (var _ in provider.ResponsesStreamingAsync(request)) { }
            else
                await provider.ResponsesAsync(request);
            Assert.Equal(original, JsonSerializer.Serialize(request));
        }
        else
        {
            var request = JsonSerializer.Deserialize<MessagesRequest>(json, JsonSerializerOptions.Web)!;
            var original = JsonSerializer.Serialize(request);
            if (streaming)
                await foreach (var _ in provider.MessagesStreamingAsync(request, [])) { }
            else
                await provider.MessagesAsync(request, []);
            Assert.Equal(original, JsonSerializer.Serialize(request));
        }
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessagesStructuredOutputIsRejectedBeforeNetwork(bool streaming)
    {
        var request = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"tilde/ENGINE","max_tokens":20,
             "messages":[{"role":"user","content":"Hi"}],
             "output_config":{"format":{"type":"json_schema","schema":{"type":"object"}}}}
            """.Replace("ENGINE", EngineId), JsonSerializerOptions.Web)!;
        var provider = CreateProvider(NoNetwork);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (streaming)
                await foreach (var _ in provider.MessagesStreamingAsync(request, [])) { }
            else
                await provider.MessagesAsync(request, []);
        });
    }

    [Fact]
    public void HeaderScopedProviderIsRegistered()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tilde-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("X-Tilde-Key", HeaderApiKeyResolver.SupportedProviderHeaders["tilde"]);
        Assert.Equal("header-key", resolver.Resolve("TILDE"));
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, s => s.ImplementationType == typeof(TildeProvider)).Lifetime);
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("tilde"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<TildeProvider>(), registry.GetModelProvider(scope.ServiceProvider, "tilde"));
    }

    private static async Task Invoke(TildeProvider provider, string operation, CancellationToken token = default)
    {
        if (operation == "discovery") await provider.ListModels(token);
        else if (operation == "chat") await provider.CompleteChatAsync(Options(), token);
        else await foreach (var _ in provider.CompleteChatStreamingAsync(Options(), token)) { }
    }
    private static ChatCompletionOptions Options() => new()
    {
        Model = EngineId, Messages = [new() { Role = "user", Content = Element("Hi") }],
        AdditionalProperties = new() { ["trgLang"] = Element("lv") }
    };
    private static string Content(ChatCompletion completion) => Element(completion).GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void AssertAuth(HttpRequestMessage request)
    {
        Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.Null(request.Headers.Authorization);
    }
    private static AsyncCacheHelper Cache() => new(new MemoryCache(new MemoryCacheOptions()));
    private static TildeProvider CreateProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, KeyResolver? key = null)
        => new(key ?? new("test-key"), new Factory(responder), Cache());
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider) => provider == "tilde" ? Key : null;
    }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public HttpClient? Client { get; private set; }
        public HttpClient CreateClient(string name) => Client = new(new Handler(responder));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
