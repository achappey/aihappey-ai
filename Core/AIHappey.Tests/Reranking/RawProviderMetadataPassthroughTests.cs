using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Core.Providers.Together;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Reranking;

public sealed class RawProviderMetadataPassthroughTests
{
    [Theory]
    [InlineData("cohere")]
    [InlineData("together")]
    public async Task Provider_keyed_metadata_is_forwarded_at_root_with_unknown_json_unchanged(string providerId)
    {
        var metadata = ParseElement("""
            {
              "priority": 7,
              "max_tokens_per_doc": 512,
              "return_documents": false,
              "rank_fields": ["title", "body"],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "query": "metadata-query",
              "documents": ["metadata-document"],
              "top_n": 99
            }
            """);
        var request = CreateRequest(providerId, metadata);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"not-for-this-provider":true}""");
        var providerOptionsBefore = JsonSerializer.Serialize(request.ProviderOptions);

        var (payload, response) = await SendAsync(providerId, request);

        Assert.Equal("rerank-model", payload.GetProperty("model").GetString());
        Assert.Equal("query", payload.GetProperty("query").GetString());
        Assert.True(JsonElement.DeepEquals(request.Documents.Values, payload.GetProperty("documents")));
        Assert.Equal(1, payload.GetProperty("top_n").GetInt32());
        Assert.Equal(7, payload.GetProperty("priority").GetInt32());
        Assert.Equal(512, payload.GetProperty("max_tokens_per_doc").GetInt32());
        Assert.False(payload.GetProperty("return_documents").GetBoolean());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("rank_fields"),
            payload.GetProperty("rank_fields")));

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "model" or "query" or "documents" or "top_n")
                continue;

            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }

        AssertFields(payload, "model", "query", "documents", "top_n", "priority", "max_tokens_per_doc",
            "return_documents", "rank_fields", "future_object", "future_array", "future_string",
            "future_number", "future_boolean", "future_null");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty(providerId, out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("not-for-this-provider", out _));
        Assert.Equal(providerOptionsBefore, JsonSerializer.Serialize(request.ProviderOptions));
        Assert.Equal(new[] { providerId }, response.ProviderMetadata!.Keys);
    }

    [Theory]
    [InlineData("cohere", """{"priority":null,"max_tokens_per_doc":null}""")]
    [InlineData("together", """{"return_documents":null,"rank_fields":[]}""")]
    public async Task Known_nulls_and_empty_values_keep_their_omission_rules(string providerId, string metadata)
    {
        var request = CreateRequest(providerId, ParseElement(metadata));
        request.TopN = null;

        var (payload, _) = await SendAsync(providerId, request);

        Assert.False(payload.TryGetProperty("priority", out _));
        Assert.False(payload.TryGetProperty("max_tokens_per_doc", out _));
        Assert.False(payload.TryGetProperty("return_documents", out _));
        Assert.False(payload.TryGetProperty("rank_fields", out _));
        Assert.False(payload.TryGetProperty("top_n", out _));
        AssertFields(payload, "model", "query", "documents");
    }

    [Theory]
    [InlineData("cohere")]
    [InlineData("together")]
    public async Task Absent_top_n_cannot_be_overridden_by_metadata_and_unknown_null_is_preserved(string providerId)
    {
        var request = CreateRequest(providerId, ParseElement("""{"top_n":99,"future_null":null}"""));
        request.TopN = null;
        var providerOptionsBefore = JsonSerializer.Serialize(request.ProviderOptions);

        var (payload, _) = await SendAsync(providerId, request);

        Assert.False(payload.TryGetProperty("top_n", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        AssertFields(payload, "model", "query", "documents", "future_null");
        Assert.Equal(providerOptionsBefore, JsonSerializer.Serialize(request.ProviderOptions));
    }

    [Theory]
    [InlineData("cohere", """{"priority":"high"}""")]
    [InlineData("cohere", "[]")]
    [InlineData("together", """{"return_documents":"false"}""")]
    [InlineData("together", """{"rank_fields":[1]}""")]
    [InlineData("together", "[]")]
    public async Task Invalid_metadata_values_fail_before_sending(string providerId, string metadata)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = CreateRequest(providerId, ParseElement(metadata));

        await Assert.ThrowsAsync<JsonException>(() => RerankingRequestAsync(providerId, client, request));
    }

    [Theory]
    [InlineData("cohere")]
    [InlineData("together")]
    public async Task Upstream_error_body_remains_the_exception_message(string providerId)
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });

        var error = await Assert.ThrowsAsync<Exception>(() =>
            RerankingRequestAsync(providerId, client, CreateRequest(providerId, ParseElement("{}"))));

        Assert.Equal("upstream error", error.Message);
    }

    private static RerankingRequest CreateRequest(string providerId, JsonElement metadata)
    {
        var request = JsonSerializer.Deserialize<RerankingRequest>(
            """{"model":"rerank-model","query":"query","topN":1,"documents":{"type":"text","values":["first","second"]}}""")!;
        request.ProviderOptions = new Dictionary<string, JsonElement>
        {
            [providerId] = metadata
        };
        return request;
    }

    private static async Task<(JsonElement Payload, RerankingResponse Response)> SendAsync(
        string providerId,
        RerankingRequest request)
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(providerId == "cohere" ? "/v2/rerank" : "/v1/rerank", message.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            payload = ParseElement(message.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"results":[{"index":0,"relevance_score":0.9}]}""");
        });

        var response = await RerankingRequestAsync(providerId, client, request);
        Assert.NotEqual(JsonValueKind.Undefined, payload.ValueKind);
        return (payload, response);
    }

    private static Task<RerankingResponse> RerankingRequestAsync(
        string providerId,
        HttpClient client,
        RerankingRequest request)
    {
        var resolver = new StaticApiKeyResolver();
        var factory = new StaticHttpClientFactory(client);
        return providerId == "cohere"
            ? new CohereProvider(
                resolver,
                new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
                factory).RerankingRequest(request)
            : new TogetherProvider(resolver, factory).RerankingRequest(request);
    }

    private static void AssertFields(JsonElement payload, params string[] names)
        => Assert.Equal(names.Order(), payload.EnumerateObject().Select(property => property.Name).Order());

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
