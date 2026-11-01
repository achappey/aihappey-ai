using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.APIpie;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderMetadataRawPassthroughTests
{
    [Fact]
    public async Task Apipie_image_options_merge_at_root_preserve_unknown_json_and_keep_request_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/images/generations", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"b64_json":"AQID"}]}""")
            };
        });
        var metadata = ParseElement("""
            {
              "quality": "high",
              "style": "vivid",
              "seed": 12,
              "steps": 23,
              "loras": ["one", "two"],
              "strength": 0.75,
              "aspect_ratio": "1:1",
              "text_layout": [{"text":"Hello","bbox":[1,2],"future_nested":{"ok":true}}],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "prompt": "metadata-prompt",
              "n": 99,
              "size": "metadata-size",
              "response_format": "url"
            }
            """);
        var request = new ImageRequest
        {
            Model = "apipie/image-model",
            Prompt = "A landscape",
            N = 1,
            Size = "512x768",
            Seed = 34,
            AspectRatio = "16:9",
            ProviderOptions = new()
            {
                ["apipie"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateApipieProvider(client).ImageRequest(request);

        Assert.Equal("apipie/image-model", payload.GetProperty("model").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal(1, payload.GetProperty("n").GetInt32());
        Assert.Equal("512x768", payload.GetProperty("size").GetString());
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal(34, payload.GetProperty("seed").GetInt32());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("high", payload.GetProperty("quality").GetString());
        Assert.Equal("vivid", payload.GetProperty("style").GetString());
        Assert.Equal(23, payload.GetProperty("steps").GetInt32());
        Assert.Equal(2, payload.GetProperty("loras").GetArrayLength());
        Assert.Equal(0.75, payload.GetProperty("strength").GetDouble());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("text_layout"), payload.GetProperty("text_layout")));
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(1, payload.GetProperty("future_object").GetProperty("nested")[0].GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("apipie", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Apipie_known_nulls_and_defaults_keep_existing_omission_and_validation()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"b64_json":"AQID"}]}""")
            };
        });
        var request = new ImageRequest
        {
            Model = "apipie/image-model",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["apipie"] = ParseElement(
                    """{"quality":null,"style":null,"image":null,"seed":null,"steps":null,"loras":null,"strength":null,"aspect_ratio":null,"text_layout":null,"future_null":null}""")
            }
        };

        await CreateApipieProvider(client).ImageRequest(request);

        Assert.Equal("apipie/image-model", payload.GetProperty("model").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal(1, payload.GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(new[] { "model", "prompt", "n", "response_format", "future_null" }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions["apipie"] = ParseElement("""{"quality":42}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateApipieProvider(invalidClient).ImageRequest(request));
        request.ProviderOptions["apipie"] = ParseElement("""{"text_layout":[{"bbox":"invalid"}]}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateApipieProvider(invalidClient).ImageRequest(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public async Task Apipie_absent_image_options_omit_optional_fields(string? metadata)
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"b64_json":"AQID"}]}""")
            };
        });

        await CreateApipieProvider(client).ImageRequest(new ImageRequest
        {
            Model = "apipie/image-model",
            Prompt = "A landscape",
            ProviderOptions = metadata is null ? null : new() { ["apipie"] = ParseElement(metadata) }
        });

        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"model":"apipie/image-model","prompt":"A landscape","n":1,"response_format":"b64_json"}"""),
            payload));
    }

    [Fact]
    public async Task Deepinfra_reranking_options_remain_provider_keyed_and_forward_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/inference/rerank-model", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"scores":[0.75]}""")
            };
        });
        var request = CreateRerankingRequest(ParseElement("""
            {
              "instruction": "Compare relevance",
              "service_tier": "priority",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "queries": ["override"],
              "documents": ["override"]
            }
            """));
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");
        var before = JsonSerializer.Serialize(request);

        await CreateDeepInfraProvider(client).RerankingRequest(request);

        Assert.Equal("Compare relevance", payload.GetProperty("instruction").GetString());
        Assert.Equal("priority", payload.GetProperty("service_tier").GetString());
        Assert.Equal("query", payload.GetProperty("queries")[0].GetString());
        Assert.Equal("document", payload.GetProperty("documents")[0].GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(request.ProviderOptions["deepinfra"].GetProperty(name),
                payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("deepinfra", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "queries", "documents", "instruction", "service_tier",
                "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Deepinfra_defaults_known_omissions_validation_and_errors_remain_compatible()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"scores":[0.75]}""")
            };
        });
        var request = CreateRerankingRequest(ParseElement(
            """{"instruction":"","service_tier":"","future_null":null}"""));

        await CreateDeepInfraProvider(client).RerankingRequest(request);

        Assert.False(payload.TryGetProperty("instruction", out _));
        Assert.False(payload.TryGetProperty("service_tier", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(3, payload.EnumerateObject().Count());

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions!["deepinfra"] = ParseElement("""{"instruction":42}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateDeepInfraProvider(invalidClient).RerankingRequest(request));

        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        request.ProviderOptions!["deepinfra"] = ParseElement("""{"future":true}""");
        var error = await Assert.ThrowsAsync<Exception>(() =>
            CreateDeepInfraProvider(failedClient).RerankingRequest(request));
        Assert.Equal("BadRequest: upstream failure", error.Message);
    }

    private static RerankingRequest CreateRerankingRequest(JsonElement options)
        => new()
        {
            Model = "rerank-model",
            Query = "query",
            Documents = new()
            {
                Type = "text",
                Values = ParseElement("""["document"]""")
            },
            ProviderOptions = new()
            {
                ["deepinfra"] = options
            }
        };

    private static APIpieProvider CreateApipieProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static DeepInfraProvider CreateDeepInfraProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

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
