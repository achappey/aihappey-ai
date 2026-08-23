using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Perplexity;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Embeddings;

public sealed class PerplexityProviderEmbeddingTests
{
    [Fact]
    public async Task VercelRequestUsesInt8EncodingAndDecodesSignedValues()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            var encoded = Convert.ToBase64String([0, 1, 127, 128, 255]);
            return JsonResponse($$"""
                {
                  "object":"list",
                  "data":[{"object":"embedding","index":0,"embedding":"{{encoded}}"}],
                  "model":"pplx-embed-v1-0.6b",
                  "usage":{"prompt_tokens":7,"total_tokens":7,"cost":{"total_cost":0.001} }
                }
                """, new() { ["x-request-id"] = "pplx-1" });
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "pplx-embed-v1-0.6b",
            Values = ["hello"],
            ProviderOptions = new()
            {
                ["perplexity"] = JsonSerializer.SerializeToElement(new
                {
                    dimensions = 128,
                    custom_option = "forwarded"
                })
            }
        });

        Assert.NotNull(captured);
        Assert.Equal("https://api.perplexity.ai/v1/embeddings", captured.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("base64_int8", body.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal(128, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("forwarded", body.RootElement.GetProperty("custom_option").GetString());
        Assert.Equal([0f, 1f, 127f, -128f, -1f], response.Embeddings.Single());
        Assert.Equal(7, response.Usage!.Tokens);
        Assert.Equal("pplx-1", response.Response!.Headers!["x-request-id"]);
        Assert.True(response.ProviderMetadata!.ContainsKey("perplexity"));
    }

    [Fact]
    public async Task OpenAIContextualRequestPreservesBase64AndFlattensDocuments()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "object":"list",
                  "data":[
                    {"object":"list","index":0,"data":[
                      {"object":"embedding","index":0,"embedding":"AAE="},
                      {"object":"embedding","index":1,"embedding":"AgM="}
                    ]},
                    {"object":"list","index":1,"data":[
                      {"object":"embedding","index":0,"embedding":"BAU="}
                    ]}
                  ],
                  "model":"pplx-embed-context-v1-0.6b",
                  "usage":{"prompt_tokens":11,"total_tokens":11}
                }
                """);
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "pplx-embed-context-v1-0.6b",
            Input = JsonSerializer.SerializeToElement(new[]
            {
                new[] { "document one chunk one", "document one chunk two" },
                new[] { "document two" }
            }),
            Dimensions = 128,
            EncodingFormat = "base64_int8"
        });

        Assert.NotNull(captured);
        Assert.Equal("https://api.perplexity.ai/v1/contextualizedembeddings", captured.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal(2, body.RootElement.GetProperty("input").GetArrayLength());
        Assert.Equal(2, body.RootElement.GetProperty("input")[0].GetArrayLength());
        Assert.Equal(["AAE=", "AgM=", "BAU="], response.Data.Select(x => x.Embedding.GetString()));
        Assert.Equal([0, 1, 2], response.Data.Select(x => x.Index));
        Assert.Equal("perplexity/pplx-embed-context-v1-0.6b", response.Model);
        Assert.Equal(11, response.Usage.PromptTokens);
    }

    [Fact]
    public async Task VercelRequestRejectsBinaryEncodingBeforeTransport()
    {
        var sent = false;
        var provider = CreateProvider((_, _) =>
        {
            sent = true;
            return Task.FromResult(JsonResponse("{}"));
        });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.EmbeddingRequestAsync(new EmbeddingRequest
            {
                Model = "pplx-embed-v1-0.6b",
                Values = ["hello"],
                ProviderOptions = new()
                {
                    ["perplexity"] = JsonSerializer.SerializeToElement(new
                    {
                        encoding_format = "base64_binary"
                    })
                }
            }));

        Assert.Contains("cannot be represented", exception.Message);
        Assert.False(sent);
    }

    private static PerplexityProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new PerplexityProvider(new StaticApiKeyResolver(), cache, new StaticHttpClientFactory(client));
    }

    private static HttpResponseMessage JsonResponse(string json, Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        foreach (var header in headers ?? [])
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }

    private sealed record CapturedRequest(Uri Uri, string Body)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => new(
                request.RequestUri!,
                await request.Content!.ReadAsStringAsync(cancellationToken));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
