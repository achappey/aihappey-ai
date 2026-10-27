using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.HuggingFace;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.HuggingFace;

public sealed class HuggingFaceProviderEmbeddingTests
{
    [Fact]
    public async Task Embedding_request_uses_feature_extraction_and_maps_batch_vectors()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse(
                """[[0.1,0.2],[0.3,0.4]]""",
                headers: new() { ["x-request-id"] = "embedding-request" });
        });

        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "huggingface/org/model",
            Values = ["first", "second"]
        });

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal(
            "https://router.huggingface.co/hf-inference/models/org/model/pipeline/feature-extraction",
            captured.Uri.AbsoluteUri);
        Assert.Equal("Bearer", captured.Authorization?.Scheme);
        Assert.Equal("test-key", captured.Authorization?.Parameter);

        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal(
            ["first", "second"],
            body.RootElement.GetProperty("inputs").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal([0.1f, 0.2f], response.Embeddings.First());
        Assert.Equal([0.3f, 0.4f], response.Embeddings.Last());
        Assert.Null(response.Usage);
        Assert.Equal("embedding-request", response.Response!.Headers!["x-request-id"]);
        Assert.True(response.ProviderMetadata!.ContainsKey("huggingface"));
    }

    [Fact]
    public async Task OpenAI_embedding_request_maps_single_input_and_base64_format()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            return JsonResponse("""[[0.25,0.5]]""");
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "org/model",
            Input = JsonSerializer.SerializeToElement("hello"),
            EncodingFormat = "base64"
        });

        Assert.NotNull(captured);
        Assert.Equal(
            "https://router.huggingface.co/hf-inference/models/org/model/pipeline/feature-extraction",
            captured.Uri.AbsoluteUri);

        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("hello", body.RootElement.GetProperty("inputs").GetString());
        Assert.Equal("huggingface/org/model", response.Model);
        Assert.Equal("AACAPgAAAD8=", response.Data.Single().Embedding.GetString());
    }

    [Fact]
    public async Task Embedding_request_rejects_dimensions_not_supported_by_feature_extraction()
    {
        var provider = CreateProvider((_, _) =>
            throw new InvalidOperationException("The request should not be sent."));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.EmbeddingRequestAsync(new EmbeddingRequest
            {
                Model = "org/model",
                Values = ["hello"],
                ProviderOptions = new()
                {
                    ["huggingface"] = JsonSerializer.SerializeToElement(new { dimensions = 128 })
                }
            }));
    }

    private static HuggingFaceProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var factory = new StaticHttpClientFactory(
            new HttpClient(new StaticResponseHttpMessageHandler(responder)));
        return new HuggingFaceProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            factory);
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
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
