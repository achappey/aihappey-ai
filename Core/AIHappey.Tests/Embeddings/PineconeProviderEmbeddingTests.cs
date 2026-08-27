using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Pinecone;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Embeddings;

public sealed class PineconeProviderEmbeddingTests
{
    [Fact]
    public async Task VercelRequestMapsPayloadParametersAndDenseResponse()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "model":"llama-text-embed-v2",
                  "vector_type":"dense",
                  "data":[{"values":[1.0,2.0]},{"values":[3.0,4.0]}],
                  "usage":{"total_tokens":9}
                }
                """, new() { ["x-request-id"] = "pinecone-1" });
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "pinecone/llama-text-embed-v2",
            Values = ["first", "second"],
            ProviderOptions = new()
            {
                ["pinecone"] = JsonSerializer.SerializeToElement(new
                {
                    inputType = "passage",
                    truncate = "END",
                    parameters = new
                    {
                        input_type = "query",
                        custom_parameter = 12
                    }
                })
            }
        });

        Assert.NotNull(captured);
        Assert.Equal("https://api.pinecone.io/embed", captured.Uri.AbsoluteUri);
        Assert.Equal("test-key", captured.ApiKey);
        Assert.Equal("2026-04", captured.ApiVersion);

        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("llama-text-embed-v2", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("first", body.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString());
        Assert.Equal("second", body.RootElement.GetProperty("inputs")[1].GetProperty("text").GetString());
        var parameters = body.RootElement.GetProperty("parameters");
        Assert.Equal("passage", parameters.GetProperty("input_type").GetString());
        Assert.Equal("END", parameters.GetProperty("truncate").GetString());
        Assert.Equal(12, parameters.GetProperty("custom_parameter").GetInt32());

        Assert.Equal([1f, 2f], response.Embeddings.First());
        Assert.Equal([3f, 4f], response.Embeddings.Last());
        Assert.Equal(9, response.Usage!.Tokens);
        Assert.Equal("pinecone-1", response.Response!.Headers!["x-request-id"]);
        Assert.Equal("dense", response.ProviderMetadata!["pinecone"].GetProperty("vector_type").GetString());
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task OpenAIRequestMapsNativeOptionsAndResponse()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "model":"multilingual-e5-large",
                  "vector_type":"dense",
                  "data":[{"values":[0.25,-0.5]}],
                  "usage":{"total_tokens":3}
                }
                """);
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "pinecone/multilingual-e5-large",
            Input = JsonSerializer.SerializeToElement("hello"),
            EncodingFormat = "float",
            AdditionalProperties = new()
            {
                ["parameters"] = JsonSerializer.SerializeToElement(new { truncate = "NONE" }),
                ["input_type"] = JsonSerializer.SerializeToElement("query")
            }
        });

        Assert.NotNull(captured);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("multilingual-e5-large", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", body.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString());
        Assert.Equal("query", body.RootElement.GetProperty("parameters").GetProperty("input_type").GetString());
        Assert.Equal("NONE", body.RootElement.GetProperty("parameters").GetProperty("truncate").GetString());

        Assert.Equal("pinecone/multilingual-e5-large", response.Model);
        Assert.Equal(0, response.Data.Single().Index);
        Assert.Equal(0.25f, response.Data.Single().Embedding[0].GetSingle());
        Assert.Equal(3, response.Usage.PromptTokens);
        Assert.Equal(3, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task SparseResponseIsRejectedClearly()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""
            {
              "model":"pinecone-sparse-english-v0",
              "vector_type":"sparse",
              "data":[{"sparse_values":[1.0],"sparse_indices":[1]}],
              "usage":{"total_tokens":1}
            }
            """)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIEmbeddingRequestAsync(CreateOpenAIRequest()));

        Assert.Contains("sparse embeddings", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedDenseResponseIsRejected()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""
            {
              "model":"llama-text-embed-v2",
              "vector_type":"dense",
              "data":[{"values":[1.0,"invalid"]}],
              "usage":{"total_tokens":1}
            }
            """)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIEmbeddingRequestAsync(CreateOpenAIRequest()));

        Assert.Contains("invalid response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TokenAndBase64InputsAreRejectedBeforeTransport()
    {
        var calls = 0;
        var provider = CreateProvider((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse("{}"));
        });

        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "llama-text-embed-v2",
            Input = JsonSerializer.SerializeToElement(new[] { 1, 2, 3 })
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "llama-text-embed-v2",
            Input = JsonSerializer.SerializeToElement("hello"),
            EncodingFormat = "base64"
        }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BackendErrorIncludesStatusAndNativeBody()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"error":{"code":"QUOTA_EXCEEDED","message":"quota exhausted"},"status":429}""",
            statusCode: HttpStatusCode.TooManyRequests)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIEmbeddingRequestAsync(CreateOpenAIRequest()));

        Assert.Contains("429", exception.Message);
        Assert.Contains("quota exhausted", exception.Message);
    }

    private static OpenAIEmbeddingRequest CreateOpenAIRequest() => new()
    {
        Model = "llama-text-embed-v2",
        Input = JsonSerializer.SerializeToElement("hello")
    };

    private static PineconeProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new PineconeProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));
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

    private sealed record CapturedRequest(Uri Uri, string Body, string? ApiKey, string? ApiVersion)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => new(
                request.RequestUri!,
                await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Api-Key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("X-Pinecone-Api-Version", out var versions) ? versions.Single() : null);
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
