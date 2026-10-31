using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Jina;
using AIHappey.Core.Providers.Pinecone;
using AIHappey.Core.Providers.Pollinations;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Pollinations_unknown_metadata_is_merged_at_the_request_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Get)
                return JsonResponse("""{"data":[{"id":"openai"}]}""");

            Assert.Equal("/openai", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });
        var provider = new PollinationsProvider(new KeyResolver(), new HttpContextAccessor(), new ClientFactory(client));
        var request = new ChatRequest
        {
            Id = "chat-1",
            Model = "pollinations/openai",
            Messages =
            [
                new UIMessage
                {
                    Id = "user-1",
                    Role = Role.user,
                    Parts = [new TextUIPart { Text = "Hello" }]
                }
            ],
            ProviderMetadata = new()
            {
                ["pollinations"] = ParseElement("""
                    {
                      "reasoning_effort": "high",
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await foreach (var _ in provider.StreamAsync(request))
        {
        }

        var options = request.ProviderMetadata["pollinations"];
        foreach (var property in options.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        Assert.Equal("pollinations/openai", payload.GetProperty("model").GetString());
        Assert.Equal("high", payload.GetProperty("reasoning_effort").GetString());
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("messages").ValueKind);
        Assert.False(payload.TryGetProperty("providerMetadata", out _));
        Assert.False(payload.TryGetProperty("pollinations", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.False(request.ProviderMetadata["pollinations"].TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task Pollinations_known_metadata_validation_is_preserved()
    {
        using var client = CreateClient(message => message.Method == HttpMethod.Get
            ? JsonResponse("""{"data":[{"id":"openai"}]}""")
            : throw new InvalidOperationException("Request should not be sent."));
        var provider = new PollinationsProvider(new KeyResolver(), new HttpContextAccessor(), new ClientFactory(client));
        var request = new ChatRequest
        {
            Model = "pollinations/openai",
            Messages = [],
            ProviderMetadata = new()
            {
                ["pollinations"] = ParseElement("""{"reasoning_effort":false}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(request))
            {
            }
        });
    }

    [Fact]
    public async Task Jina_provider_options_keep_known_fields_and_forward_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/rerank", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"results":[{"index":0,"relevance_score":0.9}],"usage":{"total_tokens":1}}""");
        });
        var request = CreateRerankingRequest("jina", """
            {
              "return_documents": false,
              "return_embeddings": null,
              "truncation": true,
              "max_doc_length": 21,
              "model": "provider-model",
              "query": "provider-query",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");

        var response = await new JinaProvider(new KeyResolver(), new ClientFactory(client))
            .RerankingRequest(request);

        foreach (var property in request.ProviderOptions["jina"].EnumerateObject())
        {
            if (property.Name is "return_embeddings" or "model" or "query")
                continue;
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }
        Assert.Equal(request.Model, payload.GetProperty("model").GetString());
        Assert.Equal(request.Query, payload.GetProperty("query").GetString());
        Assert.True(JsonElement.DeepEquals(request.Documents.Values, payload.GetProperty("documents")));
        Assert.Equal(1, payload.GetProperty("top_n").GetInt32());
        Assert.False(payload.TryGetProperty("return_embeddings", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("jina", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.Contains("jina", response.ProviderMetadata!.Keys);
        Assert.DoesNotContain("another-provider", response.ProviderMetadata.Keys);
        Assert.Equal("false", JsonSerializer.Serialize(request.ProviderOptions["jina"].GetProperty("return_documents")));
    }

    [Fact]
    public async Task Pinecone_provider_options_keep_known_fields_and_forward_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/rerank", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[{"index":0,"score":0.9}]}""");
        });
        var request = CreateRerankingRequest("pinecone", """
            {
              "return_documents": false,
              "rank_fields": ["title", "body"],
              "parameters": {"nested": [{"value": 1}, [true, null, "text"]]},
              "model": "provider-model",
              "query": "provider-query",
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");

        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new PineconeProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client));
        await provider.RerankingRequest(request);

        foreach (var property in request.ProviderOptions["pinecone"].EnumerateObject())
        {
            if (property.Name is "model" or "query")
                continue;
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }
        Assert.Equal(request.Model, payload.GetProperty("model").GetString());
        Assert.Equal(request.Query, payload.GetProperty("query").GetString());
        Assert.True(JsonElement.DeepEquals(request.Documents.Values, payload.GetProperty("documents")));
        Assert.Equal(1, payload.GetProperty("top_n").GetInt32());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("pinecone", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
    }

    [Fact]
    public async Task Pinecone_null_and_empty_known_options_remain_omitted()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[]}""");
        });
        var request = CreateRerankingRequest("pinecone", """
            {"return_documents":null,"rank_fields":[],"parameters":null}
            """);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await new PineconeProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client))
            .RerankingRequest(request);

        Assert.False(payload.TryGetProperty("return_documents", out _));
        Assert.False(payload.TryGetProperty("rank_fields", out _));
        Assert.False(payload.TryGetProperty("parameters", out _));
        Assert.Equal(
            new[] { "documents", "model", "query", "top_n" },
            payload.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("jina", """{"return_documents":"false"}""")]
    [InlineData("pinecone", """{"rank_fields":[1]}""")]
    public async Task Invalid_known_provider_options_still_fail_before_sending(string providerId, string options)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = CreateRerankingRequest(providerId, options);

        if (providerId == "jina")
        {
            await Assert.ThrowsAsync<JsonException>(() =>
                new JinaProvider(new KeyResolver(), new ClientFactory(client)).RerankingRequest(request));
            return;
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        await Assert.ThrowsAsync<JsonException>(() =>
            new PineconeProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client))
                .RerankingRequest(request));
    }

    [Fact]
    public async Task Jina_error_response_body_remains_the_thrown_message()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });
        var request = CreateRerankingRequest("jina", """{"future_option":true}""");

        var error = await Assert.ThrowsAsync<Exception>(() =>
            new JinaProvider(new KeyResolver(), new ClientFactory(client)).RerankingRequest(request));

        Assert.Equal("upstream error", error.Message);
    }

    private static RerankingRequest CreateRerankingRequest(string providerId, string options)
        => new()
        {
            Model = providerId == "jina" ? "jina/jina-reranker-v3" : "pinecone/rerank-v0",
            Query = "query",
            TopN = 1,
            Documents = new()
            {
                Type = "text",
                Values = ParseElement("""["first","second"]""")
            },
            ProviderOptions = new()
            {
                [providerId] = ParseElement(options)
            }
        };

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new Handler(send));

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
