using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.CanRouter;
using AIHappey.Core.Providers.Hush;
using AIHappey.Core.Providers.MintRouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Text;

namespace AIHappey.Tests.NewRouters;

public sealed class NewRouterProviderTests
{
    [Fact]
    public void Identifiers_are_stable()
    {
        Assert.Equal("mintrouter", CreateMint(_ => Json("{}"), "mintrouter").GetIdentifier());
        Assert.Equal("canrouter", CreateCan(_ => Json("{}"), "canrouter").GetIdentifier());
        Assert.Equal("hush", CreateHush(_ => Json("{}"), "hush").GetIdentifier());
    }

    [Fact]
    public async Task MintRouter_lists_models_with_bearer_auth()
    {
        var provider = CreateMint(request =>
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Json("""{"data":[{"id":"claude-sonnet-4-6","owned_by":"mintrouter"}]}""");
        }, "mintrouter");

        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("mintrouter/claude-sonnet-4-6", model.Id);
    }

    [Fact]
    public async Task CanRouter_maps_models_and_embeddings()
    {
        var provider = CreateCan(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => Json("""{"data":[{"id":"canrouter-embed","owned_by":"canrouter","context_window":8192}]}"""),
            "/v1/embeddings" => Json("""{"object":"list","model":"canrouter-embed","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],"usage":{"prompt_tokens":2,"total_tokens":2}}"""),
            _ => Json("{}", HttpStatusCode.NotFound)
        }, "canrouter");

        var model = Assert.Single(await provider.ListModels());
        Assert.Equal(8192, model.ContextWindow);
        var embedding = await provider.EmbeddingRequestAsync(new EmbeddingRequest { Model = "canrouter-embed", Values = ["hello"] });
        Assert.Single(embedding.Embeddings);
    }

    [Fact]
    public async Task Hush_converts_micro_usd_per_million_pricing()
    {
        var provider = CreateHush(_ => Json("""
        {"data":[{"id":"claude-sonnet","display_name":"Claude Sonnet 5","owned_by":"anthropic","context_window":1000000,"max_output_tokens":32000,"pricing":{"unit":"micro_usd_per_million_tokens","input":"2200000","output":"11000000"}}]}
        """), "hush");

        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("hush/claude-sonnet", model.Id);
        Assert.Equal(0.0000022m, model.Pricing!.Input);
        Assert.Equal(0.000011m, model.Pricing.Output);
    }

    [Fact]
    public async Task MintRouter_video_maps_pending_and_completed_states()
    {
        var completed = false;
        var provider = CreateMint(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/videos/video_1")
                return completed
                    ? Json("""{"request_id":"video_1","status":"completed","model":"sora-2","video_url":"https://files.example/video.mp4"}""")
                    : Json("""{"request_id":"video_1","status":"queued","model":"sora-2"}""");
            if (request.RequestUri.Host == "files.example")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            return Json("{}", HttpStatusCode.NotFound);
        }, "mintrouter");

        Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus("video_1"));
        completed = true;
        var result = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus("video_1"));
        Assert.Single(result.Videos);
    }

    private static MintRouterProvider CreateMint(Func<HttpRequestMessage, HttpResponseMessage> responder, string id) => new(new KeyResolver(id), Cache(), Factory(responder));
    private static CanRouterProvider CreateCan(Func<HttpRequestMessage, HttpResponseMessage> responder, string id) => new(new KeyResolver(id), Cache(), Factory(responder));
    private static HushProvider CreateHush(Func<HttpRequestMessage, HttpResponseMessage> responder, string id) => new(new KeyResolver(id), Cache(), Factory(responder));
    private static AsyncCacheHelper Cache() => new(new MemoryCache(new MemoryCacheOptions()));
    private static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> responder) => new ClientFactory(new HttpClient(new Handler(responder)));
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver(string id) : IApiKeyResolver { public string? Resolve(string provider) => provider == id ? "test-key" : null; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
