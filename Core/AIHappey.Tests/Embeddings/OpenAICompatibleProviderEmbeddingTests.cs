using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.IONOS;
using AIHappey.Core.Providers.Nebius;
using AIHappey.Core.Providers.Together;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Embeddings;

public sealed class OpenAICompatibleProviderEmbeddingTests
{
    [Theory]
    [InlineData("together", "https://api.together.ai/v1/embeddings")]
    [InlineData("ionos", "https://openai.inference.de-txl.ionos.com/v1/embeddings")]
    [InlineData("nebius", "https://api.tokenfactory.nebius.com/v1/embeddings")]
    public async Task VercelRequestUsesProviderContractAndMapsResponse(
        string providerId,
        string expectedEndpoint)
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(providerId, async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse(
                """
                {
                  "object":"list",
                  "data":[
                    {"object":"embedding","embedding":[3.0,4.0],"index":1},
                    {"object":"embedding","embedding":[1.0,2.0],"index":0}
                  ],
                  "model":"embedding-model",
                  "usage":{"prompt_tokens":9,"total_tokens":9}
                }
                """,
                headers: new() { ["x-request-id"] = "req_embeddings" });
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "embedding-model",
            Values = ["first", "second"],
            ProviderOptions = new()
            {
                [providerId] = JsonSerializer.SerializeToElement(new
                {
                    dimensions = 256,
                    custom_option = "forwarded"
                })
            }
        });

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal(expectedEndpoint, captured.Uri.AbsoluteUri);
        Assert.Equal("Bearer", captured.Authorization?.Scheme);
        Assert.Equal("test-key", captured.Authorization?.Parameter);

        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("embedding-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(["first", "second"], body.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("float", body.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal(256, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("forwarded", body.RootElement.GetProperty("custom_option").GetString());
        Assert.False(body.RootElement.TryGetProperty(providerId, out _));

        Assert.Equal([1f, 2f], response.Embeddings.First());
        Assert.Equal([3f, 4f], response.Embeddings.Last());
        Assert.Equal(9, response.Usage!.Tokens);
        Assert.Equal("req_embeddings", response.Response!.Headers!["x-request-id"]);
        Assert.True(response.ProviderMetadata!.ContainsKey(providerId));
        Assert.Equal(JsonValueKind.Object, response.ProviderMetadata[providerId].ValueKind);
        Assert.Empty(response.Warnings);
    }

    [Theory]
    [InlineData("together")]
    [InlineData("ionos")]
    [InlineData("nebius")]
    public async Task OpenAIRequestPreservesRawFieldsAndQualifiesResponseModel(string providerId)
    {
        string? requestBody = null;
        var provider = CreateProvider(providerId, async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                """
                {
                  "object":"list",
                  "data":[{"object":"embedding","embedding":[0.25,0.5],"index":0}],
                  "model":"native-model",
                  "usage":{"prompt_tokens":3,"total_tokens":3}
                }
                """);
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "native-model",
            Input = JsonSerializer.SerializeToElement("hello"),
            AdditionalProperties = new()
            {
                ["service_tier"] = JsonSerializer.SerializeToElement("flex")
            }
        });

        using var body = JsonDocument.Parse(requestBody!);
        Assert.Equal("hello", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("flex", body.RootElement.GetProperty("service_tier").GetString());
        Assert.Equal($"{providerId}/native-model", response.Model);
        Assert.Equal(3, response.Usage.PromptTokens);
        Assert.Equal(0.25f, response.Data.Single().Embedding[0].GetSingle());
    }

    private static IModelProvider CreateProvider(
        string providerId,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var factory = new StaticHttpClientFactory(
            new HttpClient(new StaticResponseHttpMessageHandler(responder)));
        var resolver = new StaticApiKeyResolver();
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return providerId switch
        {
            "together" => new TogetherProvider(resolver, factory),
            "ionos" => new IONOSProvider(resolver, cache, factory),
            "nebius" => new NebiusProvider(resolver, factory, cache),
            _ => throw new ArgumentOutOfRangeException(nameof(providerId), providerId, null)
        };
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        foreach (var header in headers ?? [])
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return response;
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string Body)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => new(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization,
                await request.Content!.ReadAsStringAsync(cancellationToken));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
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
