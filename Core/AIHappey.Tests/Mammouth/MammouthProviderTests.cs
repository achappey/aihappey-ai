using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Mammouth;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Mammouth;

public sealed class MammouthProviderTests
{
    private const string Catalog = """
        {"data":[
          {"id":"gpt-4.1","object":"model","created":1,"owned_by":"openai",
           "model_info":{"max_input_tokens":1000000,"input_cost_per_token":0.000002}},
          {"id":"text-embedding-3-small","model_info":{"input_cost_per_token":0.00000002}},
          {"id":"dall-e-3"},
          {"id":"gpt-4.1"},{"id":""},{"id":42},null
        ]}
        """;
    private const string ChatResult = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"gpt-4.1",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}
        """;
    private const string EmbeddingResult = """
        {"object":"list","model":"text-embedding-3-small",
         "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
         "usage":{"prompt_tokens":2,"total_tokens":2}}
        """;

    [Fact]
    public async Task DiscoveryUsesOfficialPublicCatalogAndMapsNativeIdsAndTypes()
    {
        var calls = 0;
        var provider = Create((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.mammouth.ai/public/models", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json(Catalog));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["mammouth/gpt-4.1", "mammouth/text-embedding-3-small", "mammouth/dall-e-3"],
            models.Select(model => model.Id));
        Assert.Equal(["language", "embedding", "image"], models.Select(model => model.Type));
        Assert.All(models, model => Assert.Equal("Mammouth", model.OwnedBy));
        Assert.Null(models[0].Pricing);
        Assert.Equal(3, (await provider.ListModels()).Count());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeySkipsDiscoveryAndRejectsExecution(string? key)
    {
        var provider = Create(NoNetwork, key);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteChatAsync(new() { Model = "gpt-4.1" }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    public async Task MalformedCatalogFailsExplicitly(string body)
        => await Assert.ThrowsAsync<JsonException>(() =>
            Create((_, _) => Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task ChatPreservesNativeParametersToolsAndUsage()
    {
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message, "v1/chat/completions");
            var body = await Body(message, token);
            Assert.Equal("gpt-4.1", body.GetProperty("model").GetString());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.Equal("function", body.GetProperty("tools")[0].GetProperty("type").GetString());
            Assert.False(body.TryGetProperty("metadata", out _));
            Assert.False(body.TryGetProperty("unrelated", out _));
            return Json(ChatResult);
        });
        var result = await provider.CompleteChatAsync(new()
        {
            Model = "mammouth/gpt-4.1",
            AdditionalProperties = new()
            {
                ["reasoning_effort"] = Element("high"),
                ["tools"] = Element(new[] { new { type = "function", function = new { name = "lookup", parameters = new { type = "object" } } } })
            },
            Metadata = new()
            {
                ["mammouth"] = Element(new { future_option = new { enabled = true } }),
                ["other"] = Element(new { unrelated = true })
            }
        });
        var json = Element(result);
        Assert.Equal("mammouth/gpt-4.1", result.Model);
        Assert.Equal("Hello", json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(3, json.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task StreamingPreservesTextFinishAndNativeUsage()
    {
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message, "v1/chat/completions");
            var body = await Body(message, token);
            Assert.Equal("gpt-4.1", body.GetProperty("model").GetString());
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-4.1","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-4.1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-4.1","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

                    data: [DONE]

                    """, Encoding.UTF8, "text/event-stream")
            };
        });
        var updates = new List<JsonElement>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new()
                       {
                           Model = "mammouth/gpt-4.1",
                           AdditionalProperties = new() { ["stream_options"] = Element(new { include_usage = true }) }
                       }))
            updates.Add(Element(update));
        Assert.Equal(3, updates.Count);
        Assert.Equal("Hello", updates[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal("stop", updates[1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(3, updates[2].GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingsMapNativeIdsOptionsAndUsage(bool vercel)
    {
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message, "v1/embeddings");
            var body = await Body(message, token);
            Assert.Equal("text-embedding-3-small", body.GetProperty("model").GetString());
            Assert.True(body.GetProperty("future_option").GetBoolean());
            return Json(EmbeddingResult);
        });
        if (vercel)
        {
            var result = await provider.EmbeddingRequestAsync(new()
            {
                Model = "mammouth/text-embedding-3-small", Values = ["Hello"],
                ProviderOptions = new() { ["mammouth"] = Element(new { future_option = true }) }
            });
            Assert.Equal([0.1f, 0.2f], result.Embeddings.Single());
            Assert.Equal(2, result.Usage!.Tokens);
        }
        else
        {
            var result = await provider.OpenAIEmbeddingRequestAsync(new()
            {
                Model = "mammouth/text-embedding-3-small", Input = Element("Hello"),
                AdditionalProperties = new() { ["future_option"] = Element(true) }
            });
            Assert.Equal("mammouth/text-embedding-3-small", result.Model);
            Assert.Equal(2, result.Usage.TotalTokens);
        }
    }

    [Fact]
    public async Task OpenAIImagesTranslatePromptAndInlineUrlWithoutInjectingDefaults()
    {
        var options = new OpenAIImageGenerationRequest
        {
            Model = "mammouth/google/gemini-2.5-flash-image", Prompt = "A cat", ResponseFormat = "url",
            AdditionalProperties = new()
            {
                ["future_option"] = Element(new { enabled = true }),
                ["image_config"] = Element(new { aspect_ratio = "16:9" }),
                ["messages"] = Element(new[] { new { role = "user", content = "wrong" } })
            }
        };
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message, "v1/chat/completions");
            var body = await Body(message, token);
            Assert.Equal("google/gemini-2.5-flash-image", body.GetProperty("model").GetString());
            Assert.Equal("A cat", body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("user", body.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.False(body.TryGetProperty("prompt", out _));
            Assert.False(body.TryGetProperty("response_format", out _));
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.Equal("16:9", body.GetProperty("image_config").GetProperty("aspect_ratio").GetString());
            Assert.False(body.TryGetProperty("size", out _));
            Assert.False(body.TryGetProperty("quality", out _));
            Assert.False(body.TryGetProperty("modalities", out _));
            return Json("""
                {"created":1,"choices":[{"message":{"role":"assistant","content":"A cat",
                 "images":[{"image_url":{"url":"data:image/webp;base64,AQID"}}]}}]}
                """);
        });
        var result = await provider.OpenAIImageGenerationRequestAsync(options);
        Assert.Equal("data:image/webp;base64,AQID", Assert.Single(result.Data!).Url);
        Assert.Equal(1, result.Created);
        Assert.Null(result.Usage);
        Assert.Equal("mammouth/google/gemini-2.5-flash-image", options.Model);
        Assert.Equal("url", options.ResponseFormat);
    }

    [Fact]
    public async Task VercelImagesMapStandardInputsAndNativeOptionsWithoutMutation()
    {
        var request = new ImageRequest
        {
            Model = "mammouth/gemini-3.1-flash-image-preview", Prompt = "A cat", N = 2, Size = "1024x1024", Seed = 7,
            ProviderOptions = new() { ["mammouth"] = Element(new { quality = "hd", future_option = true, n = 9 }) }
        };
        var original = request.ProviderOptions["mammouth"].GetRawText();
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message, "v1/chat/completions");
            var body = await Body(message, token);
            Assert.False(body.TryGetProperty("response_format", out _));
            Assert.Equal("A cat", body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal(2, body.GetProperty("n").GetInt32());
            Assert.Equal(7, body.GetProperty("seed").GetInt32());
            Assert.Equal("hd", body.GetProperty("quality").GetString());
            Assert.True(body.GetProperty("future_option").GetBoolean());
            var response = Json("""
                {"created":1,"choices":[
                  {"message":{"content":"First","images":[{"image_url":{"url":"data:image/webp;base64,AQID"}}]}},
                  {"message":{"images":[{"image_url":{"url":"data:image/jpeg;base64,BAUG"}}]}}
                 ],"native_field":{"value":42},"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
                """);
            response.Headers.Add("x-request-id", "request-1");
            return response;
        });
        var result = await provider.ImageRequest(request);
        Assert.Equal(["data:image/webp;base64,AQID", "data:image/jpeg;base64,BAUG"], result.Images);
        Assert.Equal(5, result.Usage!.TotalTokens);
        Assert.Equal(2, result.Usage.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal(42, result.ProviderMetadata!["mammouth"].GetProperty("native_field").GetProperty("value").GetInt32());
        Assert.Equal("request-1", result.Response.Headers!["x-request-id"]);
        Assert.Equal(original, request.ProviderOptions["mammouth"].GetRawText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"No image\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"images\":{}}}]}")]
    public async Task EmptyImageResponsesFailExplicitly(string body)
        => await Assert.ThrowsAsync<JsonException>(() =>
            Create((_, _) => Task.FromResult(Json(body))).OpenAIImageGenerationRequestAsync(
                new() { Model = "dall-e-3", Prompt = "A cat" }));

    [Fact]
    public async Task OpenAIImagesExtractBase64FromEveryChoiceAndImage()
    {
        var provider = Create((message, _) =>
        {
            AssertAuth(message, "v1/chat/completions");
            return Task.FromResult(Json("""
                {"created":1,"choices":[
                  {"message":{"images":[{"image_url":{"url":"data:image/png;base64,AQID"}},
                                        {"image_url":{"url":"data:image/webp;base64,BAUG"}}]}},
                  {"message":{"images":[{"image_url":{"url":"data:image/jpeg;base64,BwgJ"}}]}}
                ],"usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10}}
                """));
        });
        var response = await provider.OpenAIImageGenerationRequestAsync(
            new() { Model = "mammouth/gemini-3.1-flash-image-preview", Prompt = "A cat" });
        Assert.Equal(["AQID", "BAUG", "BwgJ"], response.Data!.Select(image => image.B64Json));
        Assert.Equal(4, response.Usage!.InputTokens);
        Assert.Equal(6, response.Usage.OutputTokens);
        Assert.Equal(10, response.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"image_url\":{\"url\":\"https://example.com/image.png\"}}")]
    [InlineData("{\"image_url\":{\"url\":\"data:text/plain;base64,AQID\"}}")]
    [InlineData("{\"image_url\":{\"url\":\"data:image/png;base64,\"}}")]
    [InlineData("{\"image_url\":{\"url\":\"data:image/png;base64,not-base64!\"}}")]
    public async Task MalformedOrRemoteImagesAreRejectedWithoutDownloading(string image)
    {
        var calls = 0;
        var provider = Create((message, _) =>
        {
            calls++;
            AssertAuth(message, "v1/chat/completions");
            return Task.FromResult(Json("{\"choices\":[{\"message\":{\"images\":[" + image + "]}}]}"));
        });
        await Assert.ThrowsAsync<JsonException>(() => provider.OpenAIImageGenerationRequestAsync(
            new() { Model = "gemini-3.1-flash-image-preview", Prompt = "A cat" }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ErrorsAndCancellationAreNotHidden()
    {
        var provider = Create((_, _) => Task.FromResult(Json(
            """{"error":{"message":"rate limited"}}""", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("rate limited", error.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => provider.CompleteChatAsync(new() { Model = "gpt-4.1" }));
        error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.OpenAIImageGenerationRequestAsync(new() { Model = "dall-e-3", Prompt = "A cat" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(NoNetwork).ListModels(cts.Token));
    }

    [Fact]
    public async Task UnifiedMessagesAndResponsesUseExistingChatAdapters()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertAuth(request, "v1/chat/completions");
            Assert.Equal("gpt-4.1", (await Body(request, token)).GetProperty("model").GetString());
            return Json(ChatResult);
        });
        var unified = await provider.ExecuteUnifiedAsync(new() { ProviderId = "mammouth", Model = "gpt-4.1" });
        Assert.Contains("Hello", JsonSerializer.Serialize(unified));
        var response = await provider.ResponsesAsync(new AIHappey.Responses.ResponseRequest
        {
            Model = "gpt-4.1", Input = "Hi"
        });
        Assert.Equal("completed", response.Status);
        Assert.Contains("Hello", JsonSerializer.Serialize(response));
        var message = await provider.MessagesAsync(new AIHappey.Messages.MessagesRequest
        {
            Model = "gpt-4.1", MaxTokens = 20, Messages = []
        }, []);
        Assert.Contains("Hello", JsonSerializer.Serialize(message));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ResponsesStreamingUsesDocumentedChatEndpointRatherThanNativeResponses()
    {
        var provider = Create(async (request, token) =>
        {
            AssertAuth(request, "v1/chat/completions");
            var body = await Body(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal("gpt-4.1", body.GetProperty("model").GetString());
            Assert.False(body.TryGetProperty("input", out _));
            Assert.Equal("Hi", body.GetProperty("messages")[0].GetProperty("content").GetString());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-4.1","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-4.1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

                    data: [DONE]

                    """, Encoding.UTF8, "text/event-stream")
            };
        });
        var events = new List<string>();
        await foreach (var part in provider.ResponsesStreamingAsync(new()
                       {
                           Model = "gpt-4.1", Input = "Hi"
                       }))
            events.Add(JsonSerializer.Serialize(part, part.GetType()));
        Assert.Contains(events, part => part.Contains("Hello"));
        Assert.Contains(events, part => part.Contains("response.completed"));
    }

    [Fact]
    public async Task DiscoveredEntriesAreExecutableThroughTheirExistingSurfaces()
    {
        var provider = Create(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(Catalog);
            var body = await Body(request, token);
            var model = body.GetProperty("model").GetString();
            Assert.DoesNotContain("mammouth/", model);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/chat/completions" => model == "dall-e-3"
                    ? Json("""{"created":1,"choices":[{"message":{"images":[{"image_url":{"url":"data:image/png;base64,AQID"}}]}}]}""")
                    : Json(ChatResult),
                "/v1/embeddings" => Json(EmbeddingResult),
                _ => throw new InvalidOperationException("Unexpected endpoint.")
            };
        });
        foreach (var model in await provider.ListModels())
        {
            switch (model.Type)
            {
                case "language":
                    await provider.CompleteChatAsync(new() { Model = model.Id });
                    break;
                case "embedding":
                    await provider.OpenAIEmbeddingRequestAsync(new() { Model = model.Id, Input = Element("Hello") });
                    break;
                case "image":
                    await provider.ImageRequest(new() { Model = model.Id, Prompt = "A cat" });
                    break;
                default:
                    Assert.Fail("Discovery exposed an unsupported surface.");
                    break;
            }
        }
    }

    [Fact]
    public async Task UnsupportedImageRequestsFailBeforeNetworkAccess()
    {
        var provider = Create(NoNetwork);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.OpenAIImageGenerationRequestAsync(new() { Model = "dall-e-3", Prompt = "A cat", Stream = true }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.ImageRequest(new() { Model = "dall-e-3", Prompt = "A cat", Mask = new() }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.ImageRequest(new()
            {
                Model = "dall-e-3", Prompt = "A cat",
                ProviderOptions = new() { ["mammouth"] = Element("invalid") }
            }));
    }

    [Fact]
    public void HeaderAndScopedRegistrationResolveMammouth()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Mammouth-Key"] = "  test-key  ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("Mammouth"));
        Assert.Equal("X-Mammouth-Key", HeaderApiKeyResolver.SupportedProviderHeaders["mammouth"]);
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            descriptor => descriptor.ImplementationType == typeof(MammouthProvider)).Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(NoNetwork)));
        services.AddSingleton(new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("mammouth"));
        Assert.IsType<MammouthProvider>(registry.GetModelProvider(scope.ServiceProvider, "mammouth"));
    }

    private static MammouthProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(handler)),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage message, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
    private static void AssertAuth(HttpRequestMessage message, string path)
    {
        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal($"https://api.mammouth.ai/{path}", message.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", message.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", message.Headers.Authorization?.Parameter);
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "mammouth" ? key : null;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
