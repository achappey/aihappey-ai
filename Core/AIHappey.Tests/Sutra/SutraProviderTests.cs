using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Sutra;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Sutra;

public sealed class SutraProviderTests
{
    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"sutra-v2",
         "choices":[{"index":0,"message":{"role":"assistant","content":"नमस्ते"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;

    private const string Stream = """
        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"sutra-v2","choices":[{"index":0,"delta":{"content":"नमस्ते"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"sutra-v2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

        data: [DONE]

        """;

    private static JsonElement NativeOptions => Element(new
    {
        online_search = true,
        search_location = new { uule = "native-location", country_code = "IN", language_code = "hi", city = "Mumbai" }
    });

    [Fact]
    public async Task CatalogContainsOnlyDocumentedChatModelsAndEachIsUsable()
    {
        var requested = new List<string>();
        var provider = Create(async (request, token) =>
        {
            AssertRequest(request);
            var body = await Body(request, token);
            var model = body.GetProperty("model").GetString()!;
            requested.Add(model);
            return Json(Completion.Replace("sutra-v2", model));
        });

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["sutra/sutra-v2", "sutra/sutra-r0"], models.Select(model => model.Id));
        Assert.All(models, model => Assert.Equal("language", model.Type));
        Assert.Empty(requested);
        foreach (var model in models)
        {
            var response = await provider.CompleteChatAsync(new() { Model = model.Id.SplitModelId().Model });
            Assert.Equal(model.Id, response.Model);
        }
        Assert.Equal(["sutra-v2", "sutra-r0"], requested);
    }

    [Fact]
    public async Task CatalogDoesNotRequireCredentialsOrNetworkAndHonorsCancellation()
    {
        var provider = Create(NoNetwork, key: null);
        Assert.Equal(2, (await provider.ListModels()).Count());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatPreservesNativeNestedOptionsTextAndReturnedUsage(bool streaming)
    {
        var options = new ChatCompletionOptions
        {
            Model = "sutra-v2",
            Stream = streaming,
            AdditionalProperties = new()
            {
                ["extra_body"] = NativeOptions,
                ["future_option"] = Element(new { enabled = false, count = 0, items = new[] { "native" } })
            }
        };
        var original = JsonSerializer.Serialize(options.AdditionalProperties);
        var provider = Create(async (request, token) =>
        {
            AssertRequest(request);
            var body = await Body(request, token);
            Assert.Equal("sutra-v2", body.GetProperty("model").GetString());
            Assert.Equal(NativeOptions.GetRawText(), body.GetProperty("extra_body").GetRawText());
            Assert.False(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.Equal(0, body.GetProperty("future_option").GetProperty("count").GetInt32());
            Assert.False(body.TryGetProperty("online_search", out _));
            Assert.False(body.TryGetProperty("temperature", out _));
            if (streaming) Assert.True(body.GetProperty("stream").GetBoolean());
            return streaming ? Sse() : Json(Completion);
        });

        if (streaming)
        {
            var updates = new List<JsonElement>();
            await foreach (var update in provider.CompleteChatStreamingAsync(options))
                updates.Add(Element(update));
            Assert.Equal("नमस्ते", updates[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
            AssertUsage(updates.Single(update => update.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object).GetProperty("usage"));
        }
        else
        {
            var response = Element(await provider.CompleteChatAsync(options));
            Assert.Equal("sutra/sutra-v2", response.GetProperty("model").GetString());
            Assert.Equal("नमस्ते", response.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
            AssertUsage(response.GetProperty("usage"));
        }
        Assert.Equal(original, JsonSerializer.Serialize(options.AdditionalProperties));
    }

    [Fact]
    public async Task DocumentedResponseWithoutUsageIsAccepted()
    {
        var withoutUsage = System.Text.Json.Nodes.JsonNode.Parse(Completion)!.AsObject();
        withoutUsage.Remove("usage");
        var provider = Create((_, _) => Task.FromResult(Json(withoutUsage.ToJsonString())));
        var result = Element(await provider.CompleteChatAsync(new() { Model = "sutra-v2" }));
        Assert.Equal("नमस्ते", result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.False(result.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task StandardAdaptersUseExistingChatTransportAndProviderOptions()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertRequest(request);
            var body = await Body(request, token);
            if (calls == 1)
                Assert.Equal(NativeOptions.GetRawText(), body.GetProperty("extra_body").GetRawText());
            return body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True
                ? Sse() : Json(Completion);
        });
        var unified = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sutra", Model = "sutra-v2",
            Metadata = new() { ["sutra"] = Element(new { extra_body = NativeOptions }) }
        });
        Assert.Equal(5, unified.NormalizedUsage!.TotalTokens);
        var response = await provider.ResponsesAsync(new ResponseRequest { Model = "sutra-v2", Input = "Hi" });
        Assert.Equal("completed", response.Status);
        Assert.NotNull(await provider.MessagesAsync(new MessagesRequest
        {
            Model = "sutra-v2", MaxTokens = 20, Messages = []
        }, []));

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(new() { ProviderId = "sutra", Model = "sutra-v2" }))
            events.Add(item);
        Assert.Contains(events, item => item.Event.Type == "text-delta");
        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(events, item => item.Event.Type == "finish").Event.Data);
        Assert.Equal(5, finish.TotalTokens);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task ChatResponsesAndMessagesStreamingUseExistingAdapters()
    {
        var calls = 0;
        var provider = Create(async (request, token) =>
        {
            calls++;
            AssertRequest(request);
            var body = await Body(request, token);
            Assert.True(body.GetProperty("stream").GetBoolean());
            if (calls == 1)
                Assert.Equal(NativeOptions.GetRawText(), body.GetProperty("extra_body").GetRawText());
            return Sse();
        });
        var chat = JsonSerializer.Deserialize<ChatRequest>("""
            {"id":"chat-1","model":"sutra/sutra-v2",
             "messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hi"}]}],
             "providerMetadata":{"sutra":{"extra_body":{"online_search":true,
               "search_location":{"uule":"native-location","country_code":"IN","language_code":"hi","city":"Mumbai"}}}}}
            """, JsonSerializerOptions.Web)!;
        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(chat))
            parts.Add(part);
        Assert.Equal("नमस्ते", string.Concat(parts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta)));
        var messages = new List<MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new MessagesRequest
        {
            Model = "sutra-v2", MaxTokens = 20, Messages = []
        }, []))
            messages.Add(part);
        Assert.NotEmpty(messages);
        var responses = new List<AIHappey.Responses.Streaming.ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new ResponseRequest { Model = "sutra-v2", Input = "Hi" }))
            responses.Add(part);
        Assert.NotEmpty(responses);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCredentialsFailBeforeNetwork(bool streaming)
    {
        var provider = Create(NoNetwork, key: null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (streaming)
                await foreach (var _ in provider.CompleteChatStreamingAsync(new() { Model = "sutra-v2" })) { }
            else
                await provider.CompleteChatAsync(new() { Model = "sutra-v2" });
        });
        Assert.Equal("No SUTRA API key.", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorsPreserveNativeDetails(bool streaming)
    {
        var provider = Create((_, _) => Task.FromResult(Json(
            """{"error":{"message":"native quota detail"}}""", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (streaming)
                await foreach (var _ in provider.CompleteChatStreamingAsync(new() { Model = "sutra-v2" })) { }
            else
                await provider.CompleteChatAsync(new() { Model = "sutra-v2" });
        });
        Assert.Contains("native quota detail", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportCancellationIsPropagated(bool streaming)
    {
        var provider = Create(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable.");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming)
                await foreach (var _ in provider.CompleteChatStreamingAsync(new() { Model = "sutra-v2" }, cancellation.Token)) { }
            else
                await provider.CompleteChatAsync(new() { Model = "sutra-v2" }, cancellation.Token);
        });
    }

    [Fact]
    public void HeaderAndScopedProviderRegistrationAreAvailable()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Sutra-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("X-Sutra-Key", HeaderApiKeyResolver.SupportedProviderHeaders["sutra"]);
        Assert.Equal("header-key", resolver.Resolve("SUTRA"));
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            descriptor => descriptor.ImplementationType == typeof(SutraProvider)).Lifetime);
        Assert.True(root.GetRequiredService<ProviderRegistry>().HasModelProvider("sutra"));
        Assert.IsType<SutraProvider>(scope.ServiceProvider.GetRequiredService<SutraProvider>());
    }

    private static SutraProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new KeyResolver(key), new Factory(responder));

    private static void AssertRequest(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.numeric.tech/v2/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
    }

    private static void AssertUsage(JsonElement usage)
    {
        Assert.Equal(2, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(3, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("total_tokens").GetInt32());
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse()
        => new(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "sutra" ? key : null;
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
