using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Jina;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Embeddings;

public sealed class JinaProviderEmbeddingTests
{
    [Fact]
    public async Task VercelRequestForcesFloatAndMapsNativeResponse()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "model":"jina-embeddings-v5-text-small",
                  "object":"list",
                  "usage":{"prompt_tokens":7,"total_tokens":7},
                  "data":[
                    {"object":"embedding","index":0,"embedding":[1.0,2.0]},
                    {"object":"embedding","index":1,"embedding":[3.0,4.0]}
                  ]
                }
                """, new() { ["x-request-id"] = "jina-1" });
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "jina/jina-embeddings-v5-text-small",
            Values = ["first", "second"],
            ProviderOptions = new()
            {
                ["jina"] = JsonSerializer.SerializeToElement(new
                {
                    embedding_type = "base64",
                    normalized = false,
                    truncate = true,
                    task = "retrieval.passage"
                })
            }
        });

        Assert.NotNull(captured);
        Assert.Equal("https://api.jina.ai/v1/embeddings", captured.Uri.AbsoluteUri);
        Assert.Equal("Bearer", captured.Authorization?.Scheme);
        Assert.Equal("test-key", captured.Authorization?.Parameter);

        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("jina-embeddings-v5-text-small", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("float", body.RootElement.GetProperty("embedding_type").GetString());
        Assert.False(body.RootElement.GetProperty("normalized").GetBoolean());
        Assert.True(body.RootElement.GetProperty("truncate").GetBoolean());
        Assert.Equal("retrieval.passage", body.RootElement.GetProperty("task").GetString());
        Assert.Equal(["first", "second"], body.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal([1f, 2f], response.Embeddings.First());
        Assert.Equal([3f, 4f], response.Embeddings.Last());
        Assert.Equal(7, response.Usage!.Tokens);
        Assert.Equal("jina-1", response.Response!.Headers!["x-request-id"]);
        Assert.Equal("jina-embeddings-v5-text-small", response.ProviderMetadata!["jina"].GetProperty("model").GetString());
    }

    [Fact]
    public async Task OpenAIRequestMapsBase64AndPassesNativeOptions()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "model":"jina-embeddings-v4",
                  "usage":{"total_tokens":3},
                  "data":[{"index":4,"embedding":"AACAPwAAAEA="}]
                }
                """);
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "jina/jina-embeddings-v4",
            Input = JsonSerializer.SerializeToElement(new object[]
            {
                new { text = "hello" },
                new { image = "https://example.test/image.png" }
            }),
            EncodingFormat = "base64",
            AdditionalProperties = new()
            {
                ["normalized"] = JsonSerializer.SerializeToElement(true),
                ["truncate"] = JsonSerializer.SerializeToElement(false),
                ["task"] = JsonSerializer.SerializeToElement("retrieval.query")
            }
        });

        Assert.NotNull(captured);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("base64", body.RootElement.GetProperty("embedding_type").GetString());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("input")[0].ValueKind);
        Assert.Equal("https://example.test/image.png", body.RootElement.GetProperty("input")[1].GetProperty("image").GetString());
        Assert.True(body.RootElement.GetProperty("normalized").GetBoolean());
        Assert.False(body.RootElement.GetProperty("truncate").GetBoolean());
        Assert.Equal("retrieval.query", body.RootElement.GetProperty("task").GetString());

        Assert.Equal("jina/jina-embeddings-v4", response.Model);
        Assert.Equal(4, response.Data.Single().Index);
        Assert.Equal("AACAPwAAAEA=", response.Data.Single().Embedding.GetString());
        Assert.Equal(3, response.Usage.PromptTokens);
        Assert.Equal(3, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task OpenAIRequestPreservesNativeEmbeddingTypeWhenEncodingFormatIsAbsent()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "data":[{"embedding":"AQID"}],
                  "usage":{"prompt_tokens":1,"total_tokens":1}
                }
                """);
        });

        await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "jina-embeddings-v5-omni-small",
            Input = JsonSerializer.SerializeToElement(new[] { new { pdf = "https://example.test/file.pdf" } }),
            AdditionalProperties = new()
            {
                ["embedding_type"] = JsonSerializer.SerializeToElement("ubinary")
            }
        });

        Assert.NotNull(captured);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("ubinary", body.RootElement.GetProperty("embedding_type").GetString());
        Assert.Equal("https://example.test/file.pdf", body.RootElement.GetProperty("input")[0].GetProperty("pdf").GetString());
    }

    [Fact]
    public async Task BackendErrorIncludesStatusAndNativeBody()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"detail":"rate limited"}""",
            statusCode: HttpStatusCode.TooManyRequests)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
            {
                Model = "jina-embeddings-v3",
                Input = JsonSerializer.SerializeToElement("hello")
            }));

        Assert.Contains("429", exception.Message);
        Assert.Contains("rate limited", exception.Message);
    }

    [Fact]
    public async Task MalformedSuccessResponseIsRejected()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""{"usage":{"total_tokens":1}}""")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
            {
                Model = "jina-embeddings-v3",
                Input = JsonSerializer.SerializeToElement("hello")
            }));

        Assert.Contains("invalid response", exception.Message);
    }

    private static JinaProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new JinaProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        Dictionary<string, string>? headers = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        foreach (var header in headers ?? [])
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }

    private sealed record CapturedRequest(Uri Uri, string Body, AuthenticationHeaderValue? Authorization)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => new(
                request.RequestUri!,
                await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization);
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
