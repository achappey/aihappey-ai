using System.Net;
using System.Net.Mime;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Vultr;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Vultr;

public sealed class VultrProviderModelTests
{
    [Fact]
    public async Task ListModels_discovers_all_types_and_preserves_voice_and_rag_shortcuts()
    {
        var requestedPaths = new List<string>();
        var provider = CreateProvider(request =>
        {
            requestedPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            return JsonResponse(request.RequestUri?.PathAndQuery switch
            {
                "/models/all" => """
                    {
                      "chat": [{"id":"llama-3","created":"1730000000","object":"model","owned_by":"meta","features":["chat","tools","chat"]}],
                      "audio": [{"id":"bark","created":"1730000001","price":0.01}],
                      "image": [{"id":"flux-dev","created":1730000002,"price":0.02}]
                    }
                    """,
                "/audio/voices" => """{"bark":["alloy","nova","alloy"]}""",
                "/vector_store" => """{"collections":[{"id":"docs","name":"Documentation"}]}""",
                _ => null
            });
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Contains("/models/all", requestedPaths);
        Assert.DoesNotContain("/v1/models", requestedPaths);

        var chat = Assert.Single(models.Where(model => model.Id == "vultr/llama-3"));
        Assert.Equal("language", chat.Type);
        Assert.Equal("meta", chat.OwnedBy);
        Assert.Equal(1730000000, chat.Created);
        Assert.Equal(["chat", "tools"], chat.Tags);

        var audio = Assert.Single(models.Where(model => model.Id == "vultr/bark"));
        Assert.Equal("speech", audio.Type);
        Assert.Equal("Vultr", audio.OwnedBy);
        Assert.Equal(1730000001, audio.Created);

        var image = Assert.Single(models.Where(model => model.Id == "vultr/flux-dev"));
        Assert.Equal("image", image.Type);
        Assert.Equal("Vultr", image.OwnedBy);
        Assert.Equal(1730000002, image.Created);

        Assert.Single(models.Where(model => model.Id == "vultr/bark/alloy" && model.Type == "speech"));
        Assert.Single(models.Where(model => model.Id == "vultr/bark/nova" && model.Type == "speech"));
        Assert.Single(models.Where(model => model.Id == "vultr/llama-3/docs" && model.Type == "language"));
        Assert.DoesNotContain(models, model => model.Id == "vultr/bark/docs");
        Assert.DoesNotContain(models, model => model.Id == "vultr/flux-dev/docs");
    }

    [Fact]
    public async Task ListModels_handles_missing_optional_arrays_and_invalid_entries()
    {
        var provider = CreateProvider(request => JsonResponse(request.RequestUri?.PathAndQuery switch
        {
            "/models/all" => """{"chat":[{},null,{"id":""},{"id":"valid"}],"audio":null}""",
            "/audio/voices" => """{}""",
            "/vector_store" => """{}""",
            _ => null
        }));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("vultr/valid", model.Id);
        Assert.Equal("language", model.Type);
    }

    private static HttpResponseMessage JsonResponse(string? json)
        => new(json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            Content = new StringContent(json ?? "{}", Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static VultrProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
