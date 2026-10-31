using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.PaxaLabs;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.PaxaLabs;

public sealed class PaxaLabsProviderMetadataTests
{
    [Fact]
    public async Task Translation_forwards_unknown_json_and_preserves_known_mappings()
    {
        JsonElement payload = default;
        var provider = CreateProvider(request =>
        {
            payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            Assert.Equal("/v1/translate", request.RequestUri!.AbsolutePath);
            return JsonResponse("""{"translations":[{"text":"bonjour"}]}""");
        });
        var metadata = ParseElement("""
            {
              "source":"ja",
              "target":"fr",
              "formality":"formal",
              "borrowed_words":"keep",
              "format":"html",
              "alternatives":2,
              "do_not_translate":["brand","name"],
              "instructions":"metadata instructions",
              "context":"product copy",
              "examples":[{"source":"こんにちは","target":"bonjour"}],
              "glossary":[{"source":"猫","target":"chat"}],
              "glossary_mode":"strict",
              "output":"structured",
              "future_object":{"nested":[{"value":1},[true,null,"text"]]},
              "future_array":[1,{"more":[false,2.5]},[]],
              "future_string":"unchanged",
              "future_number":12345678901234567890.123456789,
              "future_boolean":false,
              "future_null":null
            }
            """);

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "paxalabs",
            Model = "paxalabs/paxa-translation-lite-v1",
            Instructions = "request instructions",
            Input = new AIInput { Text = "こんにちは" },
            Metadata = new Dictionary<string, object?>
            {
                ["paxalabs"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        });

        Assert.Equal("こんにちは", payload.GetProperty("text").GetString());
        Assert.Equal("paxa-translation-lite-v1", payload.GetProperty("model").GetString());
        Assert.Equal("ja", payload.GetProperty("source").GetString());
        Assert.Equal("fr", payload.GetProperty("target").GetString());
        Assert.Equal("formal", payload.GetProperty("formality").GetString());
        Assert.Equal("metadata instructions", payload.GetProperty("instructions").GetString());
        Assert.Equal(2, payload.GetProperty("alternatives").GetInt32());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("do_not_translate"), payload.GetProperty("do_not_translate")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("examples"), payload.GetProperty("examples")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("glossary"), payload.GetProperty("glossary")));
        Assert.Equal("structured", payload.GetProperty("output").GetString());
        foreach (var name in new[] { "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null" })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        Assert.False(payload.TryGetProperty("providerMetadata", out _));
        Assert.False(payload.TryGetProperty("paxalabs", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal("bonjour", Assert.IsType<AITextContentPart>(response.Output!.Items![0].Content![0]).Text);
    }

    [Fact]
    public async Task Translation_keeps_defaults_null_fields_and_instruction_fallback()
    {
        JsonElement payload = default;
        var provider = CreateProvider(request =>
        {
            payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"translations":[{"text":"translated"}]}""");
        });

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "paxalabs",
            Model = "paxa-translation-lite-v1",
            Instructions = "request instructions",
            Input = new AIInput { Text = "input" }
        });

        Assert.Equal("auto", payload.GetProperty("source").GetString());
        Assert.Equal("th", payload.GetProperty("target").GetString());
        Assert.Equal("request instructions", payload.GetProperty("instructions").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("formality").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("examples").ValueKind);
        Assert.Equal(
            new[]
            {
                "alternatives", "borrowed_words", "context", "do_not_translate", "examples", "formality",
                "format", "glossary", "glossary_mode", "instructions", "model", "source", "target", "text"
            },
            payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Translation_invalid_known_metadata_type_still_fails_before_request()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<JsonException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "paxalabs",
            Model = "paxa-translation-lite-v1",
            Input = new AIInput { Text = "input" },
            Metadata = new Dictionary<string, object?>
            {
                ["paxalabs"] = ParseElement("""{"alternatives":"two"}""")
            }
        }));
    }

    [Fact]
    public async Task Translation_keeps_upstream_error_body_in_the_thrown_exception()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid translation options")
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "paxalabs",
            Model = "paxa-translation-lite-v1",
            Input = new AIInput { Text = "input" },
            Metadata = new Dictionary<string, object?>
            {
                ["paxalabs"] = ParseElement("""{"future_option":true}""")
            }
        }));

        Assert.Contains("Paxa Labs translation failed (400): invalid translation options", error.Message);
    }

    [Fact]
    public async Task Ocr_forwards_other_metadata_and_keeps_output_validation()
    {
        JsonElement payload = default;
        var provider = CreateProvider(request =>
        {
            payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            Assert.Equal("/v1/ocr", request.RequestUri!.AbsolutePath);
            return JsonResponse("""{"pages":[],"usage":{"pages":1,"credits":2}}""");
        });

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "paxalabs",
            Model = "paxa-ocr-lite-v1",
            Input = CreateFileInput(),
            Metadata = new Dictionary<string, object?>
            {
                ["paxalabs"] = ParseElement("""
                    {
                      "output":"structured",
                      "future_nested":{"items":[null,{"x":true}]},
                      "future_number":2.5
                    }
                    """)
            }
        });

        Assert.Equal("paxa-ocr-lite-v1", payload.GetProperty("model").GetString());
        Assert.Equal("structured", payload.GetProperty("output").GetString());
        Assert.Equal("AQID", payload.GetProperty("document").GetString());
        Assert.True(payload.GetProperty("future_nested").GetProperty("items")[0].ValueKind == JsonValueKind.Null);
        Assert.Equal(2.5, payload.GetProperty("future_number").GetDouble());

        var invalidProvider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));
        await Assert.ThrowsAsync<ArgumentException>(() => invalidProvider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "paxalabs",
            Model = "paxa-ocr-lite-v1",
            Input = CreateFileInput(),
            Metadata = new Dictionary<string, object?>
            {
                ["paxalabs"] = ParseElement("""{"output":"unsupported"}""")
            }
        }));
    }

    private static AIInput CreateFileInput()
        => new()
        {
            Items =
            [
                new AIInputItem
                {
                    Role = "user",
                    Content = [new AIFileContentPart { Type = "file", Data = "AQID", MediaType = "image/png" }]
                }
            ]
        };

    private static PaxaLabsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new Handler(responder));
        return new PaxaLabsProvider(
            new KeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(client));
    }

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
