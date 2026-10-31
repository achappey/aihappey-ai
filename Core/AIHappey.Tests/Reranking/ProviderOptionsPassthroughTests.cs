using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Assisters;
using AIHappey.Core.Providers.BergetAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Reranking;

public sealed class ProviderOptionsPassthroughTests
{
    [Theory]
    [InlineData("assisters")]
    [InlineData("bergetai")]
    public async Task Unknown_options_survive_and_provider_keyed_contracts_remain_intact(string providerId)
    {
        var request = CreateRequest(providerId, """
            {
                "return_documents": false,
                "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                "future_array": [1, {"more": [false, 2.5]}, []],
                "future_string": "unchanged",
                "future_number": 12345678901234567890.123456789,
                "future_boolean": false,
                "future_null": null
            }
            """);
        request.ProviderOptions!["another-provider"] = JsonSerializer.SerializeToElement(new { secret = "not-for-this-provider" });
        var before = JsonSerializer.Serialize(request);

        var (payload, response) = await SendAsync(providerId, request);

        foreach (var option in request.ProviderOptions[providerId].EnumerateObject())
            Assert.True(JsonElement.DeepEquals(option.Value, payload.GetProperty(option.Name)), option.Name);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal(new[] { providerId }, response.ProviderMetadata!.Keys);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty(providerId, out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        AssertFields(payload, "model", "query", "documents", "top_n", "return_documents",
            "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null");
    }

    [Theory]
    [InlineData("assisters", true)]
    [InlineData("assisters", false)]
    [InlineData("bergetai", true)]
    [InlineData("bergetai", false)]
    public async Task Known_options_keep_values_and_case_insensitive_deserialization(string providerId, bool returnDocuments)
    {
        var request = CreateRequest(providerId, $$"""{"RETURN_DOCUMENTS":{{returnDocuments.ToString().ToLowerInvariant()}}}""");
        if (providerId == "bergetai")
            request.ProviderOptions![providerId] = JsonSerializer.SerializeToElement(new
            {
                RETURN_DOCUMENTS = returnDocuments,
                USER = " user-id "
            });

        var (payload, _) = await SendAsync(providerId, request);

        Assert.Equal(returnDocuments, payload.GetProperty("return_documents").GetBoolean());
        Assert.False(payload.TryGetProperty("RETURN_DOCUMENTS", out _));
        if (providerId == "bergetai")
        {
            Assert.Equal(" user-id ", payload.GetProperty("user").GetString());
            AssertFields(payload, "model", "query", "documents", "top_n", "return_documents", "user");
        }
        else
            AssertFields(payload, "model", "query", "documents", "top_n", "return_documents");
    }

    [Theory]
    [InlineData("assisters", null)]
    [InlineData("assisters", "null")]
    [InlineData("assisters", "{}")]
    [InlineData("assisters", """{"return_documents":null}""")]
    [InlineData("bergetai", null)]
    [InlineData("bergetai", "null")]
    [InlineData("bergetai", "{}")]
    [InlineData("bergetai", """{"return_documents":null,"user":null}""")]
    [InlineData("bergetai", """{"user":""}""")]
    [InlineData("bergetai", """{"user":" \t "}""")]
    public async Task Defaults_and_known_null_omission_remain_compatible(string providerId, string? options)
    {
        var request = CreateRequest(providerId, options);
        request.TopN = null;

        var (payload, _) = await SendAsync(providerId, request);

        Assert.False(payload.TryGetProperty("return_documents", out _));
        Assert.False(payload.TryGetProperty("user", out _));
        if (providerId == "assisters")
        {
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("top_n").ValueKind);
            AssertFields(payload, "model", "query", "documents", "top_n");
        }
        else
            AssertFields(payload, "model", "query", "documents");
    }

    [Theory]
    [InlineData("assisters")]
    [InlineData("bergetai")]
    public async Task Normalized_fields_override_conflicts_without_losing_unrelated_options(string providerId)
    {
        var request = CreateRequest(providerId, """
            {"model":"override","query":"override","documents":["override"],"top_n":99,"future":{"a":[1,2]}}
            """);

        var (payload, response) = await SendAsync(providerId, request);

        Assert.Equal(request.Model, payload.GetProperty("model").GetString());
        Assert.Equal(request.Query, payload.GetProperty("query").GetString());
        Assert.True(JsonElement.DeepEquals(request.Documents.Values, payload.GetProperty("documents")));
        Assert.Equal(1, payload.GetProperty("top_n").GetInt32());
        Assert.True(JsonElement.DeepEquals(request.ProviderOptions![providerId].GetProperty("future"), payload.GetProperty("future")));
        Assert.Equal($"{providerId}/{request.Model}", response.Response.ModelId);
        AssertFields(payload, "model", "query", "documents", "top_n", "future");
        if (providerId == "bergetai")
        {
            Assert.Equal(1, Assert.Single(response.Ranking).Index);
            Assert.Equal(3, response.ProviderMetadata![providerId].GetProperty("usage").GetProperty("total_tokens").GetInt32());
        }
        else
            Assert.Equal(new[] { 0, 1 }, response.Ranking.Select(r => r.Index));
    }

    [Theory]
    [InlineData("assisters")]
    [InlineData("bergetai")]
    public async Task Optional_top_n_retains_provider_specific_precedence(string providerId)
    {
        var request = CreateRequest(providerId, """{"top_n":2}""");
        request.TopN = null;

        var (payload, _) = await SendAsync(providerId, request);

        if (providerId == "assisters")
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("top_n").ValueKind);
        else
            Assert.Equal(2, payload.GetProperty("top_n").GetInt32());
    }

    [Theory]
    [InlineData("assisters", """{"return_documents":"true"}""")]
    [InlineData("assisters", """{"return_documents":1}""")]
    [InlineData("assisters", "[]")]
    [InlineData("assisters", "true")]
    [InlineData("bergetai", """{"return_documents":"false"}""")]
    [InlineData("bergetai", """{"user":1}""")]
    [InlineData("bergetai", "[]")]
    [InlineData("bergetai", "42")]
    public async Task Invalid_known_options_still_fail_before_sending(string providerId, string options)
    {
        await Assert.ThrowsAsync<JsonException>(() => SendAsync(providerId, CreateRequest(providerId, options)));
    }

    [Theory]
    [InlineData("assisters", "query")]
    [InlineData("assisters", "documents")]
    [InlineData("assisters", "topN")]
    [InlineData("bergetai", "query")]
    [InlineData("bergetai", "documents")]
    [InlineData("bergetai", "topN")]
    public async Task Existing_request_validation_is_preserved(string providerId, string invalidField)
    {
        var request = CreateRequest(providerId, """{"future":[1,2]}""");
        if (invalidField == "query")
            request.Query = " ";
        else if (invalidField == "documents")
            request.Documents = new() { Type = "text", Values = JsonSerializer.SerializeToElement(Array.Empty<string>()) };
        else
            request.TopN = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(providerId, request));
    }

    [Fact]
    public async Task Assisters_document_mapping_is_unchanged()
    {
        var request = CreateRequest("assisters", """{"future":{"enabled":true}}""");
        request.Documents = new()
        {
            Type = "text",
            Values = JsonSerializer.SerializeToElement(new object[] { "first", new { text = "second", existing_ignored_field = true } })
        };

        var (payload, _) = await SendAsync("assisters", request);

        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new object[] { "first", new { text = "second" } }),
            payload.GetProperty("documents")));
        Assert.True(payload.GetProperty("future").GetProperty("enabled").GetBoolean());
    }

    private static RerankingRequest CreateRequest(string providerId, string? options)
    {
        var request = JsonSerializer.Deserialize<RerankingRequest>(
            """{"model":"rerank-model","query":"query","topN":1,"documents":{"type":"text","values":["first","second"]}}""")!;
        if (options is not null)
        {
            using var document = JsonDocument.Parse(options);
            request.ProviderOptions = new() { [providerId] = document.RootElement.Clone() };
        }
        return request;
    }

    private static void AssertFields(JsonElement payload, params string[] names)
        => Assert.Equal(names.Order(), payload.EnumerateObject().Select(p => p.Name).Order());

    private static async Task<(JsonElement Payload, RerankingResponse Response)> SendAsync(string providerId, RerankingRequest request)
    {
        JsonElement payload = default;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new Handler(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/v1/rerank", message.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-api-key", message.Headers.Authorization!.ToString());
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            using var document = JsonDocument.Parse(message.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            payload = document.RootElement.Clone();
            var resultsKey = providerId == "assisters" ? "results" : "data";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$$"""{"{{{resultsKey}}}":[{"index":0,"relevance_score":0.2},{"index":1,"relevance_score":0.9}],"usage":{"total_tokens":3}}""",
                    Encoding.UTF8, "application/json")
            };
        }));
        var resolver = new KeyResolver();
        var factory = new ClientFactory(client);
        var asyncCache = new AsyncCacheHelper(cache);
        var response = providerId == "assisters"
            ? await new AssistersProvider(resolver, asyncCache, factory).RerankingRequest(request)
            : await new BergetAIProvider(resolver, asyncCache, factory).RerankingRequest(request);
        Assert.NotEqual(JsonValueKind.Undefined, payload.ValueKind);
        return (payload, response);
    }

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
