using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.CloudflareWorkersAI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.CloudflareWorkersAI;

public sealed class CloudflareWorkersAIProviderTests
{
    [Fact]
    public async Task ListModels_uses_account_scoped_endpoint_and_preserves_native_ids()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal(
                "/client/v4/accounts/test-account/ai/v1/models",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter));
            return JsonResponse(
                """{"object":"list","data":[{"id":"@cf/meta/llama-3.1-8b-instruct","owned_by":"meta"}]}""");
        });

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal(
            "cloudflareworkersai/@cf/meta/llama-3.1-8b-instruct",
            model.Id);
        Assert.Equal("@cf/meta/llama-3.1-8b-instruct", model.Name);
        Assert.Equal("meta", model.OwnedBy);
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompleteChat_forwards_chat_options_to_openai_compatible_endpoint()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(
                "/client/v4/accounts/test-account/ai/v1/chat/completions",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter));

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("@cf/meta/llama-3.1-8b-instruct", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(0.25, body.RootElement.GetProperty("presence_penalty").GetDouble());

            return JsonResponse(
                """{"id":"completion-1","object":"chat.completion","created":1,"model":"@cf/meta/llama-3.1-8b-instruct","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}""");
        });

        var response = await provider.CompleteChatAsync(new()
        {
            Model = "@cf/meta/llama-3.1-8b-instruct",
            AdditionalProperties = new()
            {
                ["presence_penalty"] = JsonSerializer.SerializeToElement(0.25)
            }
        });

        Assert.NotNull(response.Usage);
    }

    [Fact]
    public async Task OpenAIEmbeddingRequest_uses_compatible_embeddings_endpoint_and_maps_usage()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(
                "/client/v4/accounts/test-account/ai/v1/embeddings",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter));

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("@cf/baai/bge-large-en-v1.5", body.RootElement.GetProperty("model").GetString());

            return JsonResponse(
                """{"object":"list","data":[{"object":"embedding","embedding":[0.1,0.2],"index":0}],"model":"@cf/baai/bge-large-en-v1.5","usage":{"prompt_tokens":2,"total_tokens":2}}""");
        });

        var response = await provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = "@cf/baai/bge-large-en-v1.5",
            Input = JsonSerializer.SerializeToElement("hello")
        });

        Assert.Equal(2, Assert.Single(response.Data).Embedding.GetArrayLength());
        Assert.Equal(2, response.Usage.TotalTokens);
    }

    private static CloudflareWorkersAIProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new KeyResolver(),
            new ClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            Options.Create(new CloudflareWorkersAIProviderOptions { AccountId = "test-account" }));

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

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
