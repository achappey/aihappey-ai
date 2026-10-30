using AIHappey.Core.AI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.EdgeSpeak;

public sealed partial class EdgeSpeakProviderTests
{
    private const string Catalog = """
        {"object":"list","data":[
          {"id":"Qwen/Qwen3.5-4B","created":0,"owned_by":"EdgeSpeak","supported_endpoints":["/v1/chat/completions","/v1/responses","/v1/messages"],"features":["reasoning","vision","tool_calling"],"execution_location":"remote","default_for":["/v1/chat/completions"]},
          {"id":"openbmb/VoxCPM2","owned_by":"EdgeSpeak","supported_endpoints":["/v1/audio/speech"],"supported_languages":["zh","en-US"],"execution_location":"local","default_for":["/v1/audio/speech"],"installed":false,"x_edgespeak":{"speech_language":{"open_set":true}}},
          {"id":"transcriber/model","supported_endpoints":["/v1/audio/transcriptions"],"execution_location":"local","default_for":[]},
          {"id":"EdgeSpeak/Lattice-2","supported_endpoints":["/v1/audio/alignments"]},
          {"id":"speaker/model","supported_endpoints":["/v1/speaker/embeddings"]},
          {"id":"other/model","supported_endpoints":["/v1/text/normalizations"]}
        ]}
        """;

    [Fact]
    public async Task Discovery_uses_live_endpoint_capabilities_and_keeps_canonical_IDs_and_unknown_prices()
    {
        var calls = 0;
        var provider = Create((message, _) =>
        {
            calls++;
            AssertAuth(message);
            Assert.Equal("/v1/models", message.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Catalog));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(3, models.Length);
        Assert.Equal("language", models[0].Type);
        Assert.Equal("speech", models[1].Type);
        Assert.Equal("transcription", models[2].Type);
        Assert.Equal("edgespeak/openbmb/VoxCPM2", models[1].Id);
        Assert.Contains("en-US", models[1].Tags!);
        Assert.Contains("execution:remote", models[0].Tags!);
        Assert.Contains("default:/v1/audio/speech", models[1].Tags!);
        Assert.Contains("speech_language:open_set", models[1].Tags!);
        Assert.All(models, m => Assert.Null(m.Pricing));
        await provider.ListModels();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Discovery_cache_is_partitioned_by_gateway_and_credentials()
    {
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        var calls = 0;
        Task<System.Net.Http.HttpResponseMessage> Send(System.Net.Http.HttpRequestMessage _, CancellationToken __)
        {
            calls++;
            return Task.FromResult(Json(Catalog));
        }
        await Create(Send, cache: cache).ListModels();
        await Create(Send, cache: cache, key: "other").ListModels();
        await Create(Send, cache: cache, endpoint: "https://gateway.example/v1").ListModels();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Keyless_local_gateways_are_supported_without_a_fabricated_authorization_header()
    {
        var provider = Create((message, _) =>
        {
            Assert.Null(message.Headers.Authorization);
            Assert.Equal(1118, message.RequestUri!.Port);
            Assert.Equal("/v1/models", message.RequestUri.AbsolutePath);
            return Task.FromResult(Json(Catalog));
        }, key: null, endpoint: "http://127.0.0.1:1118/v1/");
        Assert.NotEmpty(await provider.ListModels());
    }

    [Fact]
    public async Task Cancelled_discovery_does_not_make_an_HTTP_request_or_return_cached_success()
    {
        var provider = Create((_, _) => Task.FromResult(Json(Catalog)));
        await provider.ListModels();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(NoHttp).ListModels(cts.Token));
    }
}
