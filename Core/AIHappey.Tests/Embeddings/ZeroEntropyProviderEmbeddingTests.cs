using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.ZeroEntropy;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Embeddings;

public sealed class ZeroEntropyProviderEmbeddingTests
{
    [Fact]
    public async Task VercelRequestDefaultsToDocumentAndMapsNativeResponse()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "results":[
                    {"embedding":[1.0,2.0]},
                    {"embedding":[3.0,4.0]}
                  ],
                  "usage":{"total_bytes":321,"total_tokens":9}
                }
                """, new() { ["x-request-id"] = "ze-1" });
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "zeroentropy/zembed-1",
            Values = ["first", "second"],
            ProviderOptions = new()
            {
                ["zeroentropy"] = JsonSerializer.SerializeToElement(new
                {
                    dimensions = 640,
                    latency = "slow"
                })
            }
        });

        Assert.NotNull(captured);
        Assert.Equal("https://api.zeroentropy.dev/v1/models/embed", captured.Uri.AbsoluteUri);
        Assert.Equal("Bearer", captured.Authorization?.Scheme);
        Assert.Equal("test-key", captured.Authorization?.Parameter);

        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("zembed-1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("document", body.RootElement.GetProperty("input_type").GetString());
        Assert.Equal("float", body.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal(640, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("slow", body.RootElement.GetProperty("latency").GetString());
        Assert.Equal(["first", "second"], body.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal([1f, 2f], response.Embeddings.First());
        Assert.Equal([3f, 4f], response.Embeddings.Last());
        Assert.Equal(9, response.Usage!.Tokens);
        Assert.Equal("ze-1", response.Response!.Headers!["x-request-id"]);
        var metadata = response.ProviderMetadata!["zeroentropy"];
        Assert.Equal(321, metadata.GetProperty("usage").GetProperty("total_bytes").GetInt32());
    }

    [Fact]
    public async Task OpenAIRequestPreservesNativeInputTypeAndBase64Result()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""
                {
                  "results":[{"embedding":"AACAPwAAAEA="}],
                  "usage":{"total_bytes":155,"total_tokens":3}
                }
                """);
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "zeroentropy/zembed-1",
            Input = JsonSerializer.SerializeToElement("search text"),
            Dimensions = 320,
            EncodingFormat = "base64",
            AdditionalProperties = new()
            {
                ["input_type"] = JsonSerializer.SerializeToElement("query"),
                ["latency"] = JsonSerializer.SerializeToElement("fast")
            }
        });

        Assert.NotNull(captured);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("query", body.RootElement.GetProperty("input_type").GetString());
        Assert.Equal("fast", body.RootElement.GetProperty("latency").GetString());
        Assert.Equal("base64", body.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal("search text", body.RootElement.GetProperty("input").GetString());

        Assert.Equal("zeroentropy/zembed-1", response.Model);
        Assert.Equal("AACAPwAAAEA=", response.Data.Single().Embedding.GetString());
        Assert.Equal(0, response.Data.Single().Index);
        Assert.Equal(3, response.Usage.PromptTokens);
        Assert.Equal(3, response.Usage.TotalTokens);
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
                Model = "zembed-1",
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
                Model = "zembed-1",
                Input = JsonSerializer.SerializeToElement("hello")
            }));

        Assert.Contains("invalid response", exception.Message);
    }

    private static ZeroEntropyProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new ZeroEntropyProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));
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
