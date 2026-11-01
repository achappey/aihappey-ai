using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cohere;

public sealed class CohereProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Unified_provider_metadata_is_merged_at_root_without_dropping_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Get)
                return JsonResponse("""{"models":[{"name":"command-a","features":["chat"]}]}""");

            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                {
                  "id": "test",
                  "message": {
                    "role": "assistant",
                    "content": [{"type": "text", "text": "ok"}]
                  },
                  "finish_reason": "COMPLETE"
                }
                """);
        });
        var metadata = ParseElement("""
            {
              "thinking": {
                "token_budget": 256,
                "future_nested": {"items": [{"value": 1}, [true, null, "text"]]}
              },
              "citation_options": {
                "mode": "FAST",
                "future_array": [1, {"nested": [false, 2.5]}]
              },
              "priority": 7,
              "documents": null,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "temperature": 0.9
            }
            """);
        var request = new AIRequest
        {
            ProviderId = "cohere",
            Model = "command-a",
            Input = new AIInput { Text = "Hello" },
            Temperature = 0.2,
            Metadata = new Dictionary<string, object?>
            {
                ["cohere"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateProvider(client).ExecuteUnifiedAsync(request);

        Assert.Equal("command-a", payload.GetProperty("model").GetString());
        Assert.Equal(0.2, payload.GetProperty("temperature").GetDouble());
        Assert.Equal(7, payload.GetProperty("priority").GetInt32());
        Assert.Equal("enabled", payload.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(256, payload.GetProperty("thinking").GetProperty("token_budget").GetInt32());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("thinking").GetProperty("future_nested"),
            payload.GetProperty("thinking").GetProperty("future_nested")));
        Assert.Equal("FAST", payload.GetProperty("citation_options").GetProperty("mode").GetString());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("citation_options").GetProperty("future_array"),
            payload.GetProperty("citation_options").GetProperty("future_array")));

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "thinking" or "citation_options" or "priority" or "documents" or "model" or "temperature")
                continue;

            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }

        Assert.False(payload.TryGetProperty("documents", out _));
        Assert.False(payload.TryGetProperty("providerMetadata", out _));
        Assert.False(payload.TryGetProperty("cohere", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
    }

    [Fact]
    public void Vercel_provider_metadata_remains_keyed_by_provider_at_the_unified_boundary()
    {
        var cohereMetadata = ParseElement("""{"future":{"nested":[1,null,true]}}""");
        var request = new ChatRequest
        {
            Id = "chat-1",
            Messages = [],
            Model = "cohere/command-a",
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["cohere"] = cohereMetadata,
                ["another-provider"] = ParseElement("""{"untouched":true}""")
            }
        };

        var unifiedRequest = request.ToUnifiedRequest("cohere");

        Assert.True(JsonElement.DeepEquals(
            cohereMetadata,
            Assert.IsType<JsonElement>(unifiedRequest.Metadata!["cohere"])));
        Assert.True(JsonElement.DeepEquals(
            request.ProviderMetadata["another-provider"],
            Assert.IsType<JsonElement>(unifiedRequest.Metadata["another-provider"])));
    }

    [Fact]
    public async Task Unified_known_metadata_defaults_omissions_and_validation_are_preserved()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Get)
                return JsonResponse("""{"models":[{"name":"command-a","features":["chat"]}]}""");

            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                {
                  "id": "test",
                  "message": {
                    "role": "assistant",
                    "content": [{"type": "text", "text": "ok"}]
                  },
                  "finish_reason": "COMPLETE"
                }
                """);
        });
        var request = CreateRequest("""{"thinking":{"future":true},"citation_options":{},"priority":null,"documents":null}""");

        await CreateProvider(client).ExecuteUnifiedAsync(request);

        Assert.Equal("enabled", payload.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("thinking").GetProperty("token_budget").ValueKind);
        Assert.True(payload.GetProperty("thinking").GetProperty("future").GetBoolean());
        Assert.Equal("enabled", payload.GetProperty("citation_options").GetProperty("mode").GetString());
        Assert.False(payload.TryGetProperty("priority", out _));
        Assert.False(payload.TryGetProperty("documents", out _));

        using var invalidClient = CreateClient(message => message.Method == HttpMethod.Get
            ? JsonResponse("""{"models":[{"name":"command-a","features":["chat"]}]}""")
            : throw new InvalidOperationException("Request should not be sent."));
        var invalidProvider = CreateProvider(invalidClient);

        await Assert.ThrowsAsync<JsonException>(() =>
            invalidProvider.ExecuteUnifiedAsync(
                CreateRequest("""{"thinking":{"token_budget":"many"}}""")));
        await Assert.ThrowsAsync<JsonException>(() =>
            invalidProvider.ExecuteUnifiedAsync(
                CreateRequest("""{"priority":"high"}""")));
    }

    [Fact]
    public async Task Unified_upstream_errors_keep_the_existing_exception_message()
    {
        using var client = CreateClient(message => message.Method == HttpMethod.Get
            ? JsonResponse("""{"models":[{"name":"command-a","features":["chat"]}]}""")
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("upstream error")
            });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateProvider(client).ExecuteUnifiedAsync(CreateRequest("{}")));

        Assert.Equal(
            "Cohere unified request failed with HTTP 400 (Bad Request). Body: upstream error",
            error.Message);
    }

    private static AIRequest CreateRequest(string metadata)
        => new()
        {
            ProviderId = "cohere",
            Model = "command-a",
            Input = new AIInput { Text = "Hello" },
            Metadata = new Dictionary<string, object?>
            {
                ["cohere"] = ParseElement(metadata)
            }
        };

    private static CohereProvider CreateProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
