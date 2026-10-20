using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Krutrim;
using AIHappey.HeaderAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Krutrim;

public sealed class KrutrimProviderTests
{
    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"Krutrim-spectre-v2",
         "choices":[{"index":0,"message":{"role":"assistant","content":"namaste"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;

    private const string Stream = """
        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Krutrim-spectre-v2","choices":[{"index":0,"delta":{"content":"namaste"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Krutrim-spectre-v2","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

        data: [DONE]

        """;

    [Fact]
    public async Task ListModels_uses_only_documented_chat_model_identifiers()
    {
        var provider = Create(NoNetwork);

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal(
            [
                "krutrim/Krutrim-spectre-v2",
                "krutrim/Mistral-7B-Instruct",
                "krutrim/Meta-Llama-3-8B-Instruct"
            ],
            models.Select(model => model.Id));
        Assert.All(models, model =>
        {
            Assert.Equal("language", model.Type);
        });
    }

    [Fact]
    public async Task CompleteChat_uses_official_endpoint_and_preserves_native_options_and_usage()
    {
        var provider = Create(async (request, cancellationToken) =>
        {
            AssertChatRequest(request);
            Assert.Equal("false", Assert.Single(request.Headers.GetValues("X-Krutrim-Cloud-Async")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("Krutrim-spectre-v2", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("top_k").GetInt32());
            return Json(Completion);
        });
        var options = new ChatCompletionOptions
        {
            Model = "Krutrim-spectre-v2",
            AdditionalProperties = new() { ["top_k"] = JsonSerializer.SerializeToElement(7) }
        };

        var result = await provider.CompleteChatAsync(options);
        var response = JsonSerializer.SerializeToElement(result);

        Assert.Equal("krutrim/Krutrim-spectre-v2", response.GetProperty("model").GetString());
        Assert.Equal("namaste", response.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(5, response.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_preserves_native_usage()
    {
        var provider = Create((request, _) =>
        {
            AssertChatRequest(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream")
            });
        });

        var updates = new List<JsonElement>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
                       {
                           Model = "Krutrim-spectre-v2",
                           Stream = true
                       }))
            updates.Add(JsonSerializer.SerializeToElement(update));

        Assert.Equal("namaste", updates[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(5, updates.Single(update =>
                update.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            .GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void HeaderAndScopedProviderRegistrationAreAvailable()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Krutrim-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("X-Krutrim-Key", HeaderApiKeyResolver.SupportedProviderHeaders["krutrim"]);
        Assert.Equal("header-key", resolver.Resolve("KRUTRIM"));

        var services = new ServiceCollection();
        services.AddProviders();
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();

        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            descriptor => descriptor.ImplementationType == typeof(KrutrimProvider)).Lifetime);
        Assert.True(root.GetRequiredService<ProviderRegistry>().HasModelProvider("krutrim"));
        Assert.IsType<KrutrimProvider>(scope.ServiceProvider.GetRequiredService<KrutrimProvider>());
    }

    private static KrutrimProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? apiKey = "test-key")
        => new(new ApiKeyResolver(apiKey), new Factory(responder));

    private static void AssertChatRequest(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://cloud.olakrutrim.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");

    private sealed class ApiKeyResolver(string? apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "krutrim" ? apiKey : null;
    }

    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
