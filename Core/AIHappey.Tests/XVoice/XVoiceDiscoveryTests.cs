using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.XVoice;
using AIHappey.HeaderAuth;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.XVoice;

public sealed class XVoiceDiscoveryTests
{
    [Fact]
    public async Task Discovery_maps_native_types_capabilities_versions_languages_and_model_specific_voices()
    {
        var calls = 0;
        var resolver = new Resolver();
        var provider = Create((request, _) =>
        {
            calls++;
            Assert.Equal(resolver.Key, request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/v1/models"
                ? Models : Voices));
        }, resolver);
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(3, models.Length);
        var tts = Assert.Single(models, m => m.Id == "xvoice/cadence-tts");
        Assert.Equal("speech", tts.Type);
        Assert.Equal(123, tts.Created);
        Assert.Contains("streaming", tts.Tags!);
        Assert.Contains("en", tts.Tags!);
        Assert.Contains("version:2026-09-17", tts.Tags!);
        Assert.Contains("\"response_formats\":[\"wav\",\"mp3\",\"pcm\"]", tts.Description);
        Assert.Null(tts.Pricing);
        var stt = Assert.Single(models, m => m.Id == "xvoice/cadence-stt");
        Assert.Equal("transcription", stt.Type);
        Assert.Contains("realtime", stt.Tags!);
        Assert.Contains("deprecated", stt.Tags!);
        var voice = Assert.Single(models, m => m.Id == "xvoice/cadence-tts/clara");
        Assert.Contains("female", voice.Tags!);
        Assert.Contains("en-US", voice.Tags!);
        Assert.Contains("es", voice.Tags!);
        Assert.Contains("preview_url", voice.Description);
        Assert.Equal(3, (await provider.ListModels()).Count());
        Assert.Equal(2, calls);
        resolver.Key = "second-key";
        await provider.ListModels();
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Native_discovery_preserves_full_model_and_voice_metadata_and_filters()
    {
        var requests = new List<string>();
        var provider = Create((request, _) =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(Json(Voices));
        });
        await provider.GetModelsAsync("tts");
        await provider.GetModelInfoAsync("xvoice/new-model");
        var voices = await provider.GetVoicesAsync("xvoice/new-model", "es", "en-US");
        Assert.Equal("/v1/models?type=tts", requests[0]);
        Assert.Equal("/v1/models/new-model", requests[1]);
        Assert.Equal("/v1/voices?model=new-model&language=es&locale=en-US", requests[2]);
        Assert.Equal("clara", voices.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal("sample", voices.GetProperty("data")[0].GetProperty("metadata").GetProperty("custom").GetString());
    }

    [Fact]
    public async Task Model_specific_voice_discovery_does_not_assume_shared_catalogs()
    {
        var provider = Create((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return Task.FromResult(Json(Models.Replace("\"type\":\"stt\"", "\"type\":\"tts\"")));
            var model = request.RequestUri.Query;
            return Task.FromResult(Json(model.Contains("cadence-stt") ? Voices.Replace("clara", "maya") : Voices));
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Contains(models, m => m.Id == "xvoice/cadence-tts/clara");
        Assert.Contains(models, m => m.Id == "xvoice/cadence-stt/maya");
        Assert.DoesNotContain(models, m => m.Id == "xvoice/cadence-stt/clara");
    }

    [Fact]
    public async Task Missing_auth_catalog_errors_and_cancellation_are_explicit()
    {
        var resolver = new Resolver { Key = null };
        var provider = Create((_, _) => throw new InvalidOperationException("No HTTP expected."), resolver);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetModelsAsync());
        provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"code":"invalid_api_key"}}""")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("invalid_api_key", error.Message);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cts.Token));
    }

    [Fact]
    public async Task Unified_speech_routes_through_native_provider_and_scoped_options()
    {
        var speechCalls = 0;
        var provider = Create(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return Json(Models);
            if (request.RequestUri.AbsolutePath == "/v1/voices")
                return Json(Voices);
            speechCalls++;
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("es", body.GetProperty("language").GetString());
            Assert.Equal("clara", body.GetProperty("voice").GetString());
            Assert.Equal("hello", body.GetProperty("input").GetString());
            if (body.TryGetProperty("stream", out var stream) && stream.GetBoolean())
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"type\":\"speech.audio.delta\",\"audio\":\"AQID\"}\n\n" +
                        "data: {\"type\":\"speech.audio.done\"}\n\n", Encoding.UTF8, "text/event-stream")
                };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        var request = new AIRequest
        {
            ProviderId = "xvoice",
            Model = "xvoice/cadence-tts/clara",
            Input = new() { Items = [new() { Type = "message", Role = "user", Content = [new AITextContentPart { Type = "text", Text = "hello" }] }] },
            Metadata = new() { ["xvoice"] = JsonSerializer.SerializeToElement(new { language = "es" }) }
        };
        Assert.Equal("completed", (await provider.ExecuteUnifiedAsync(request)).Status);
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(request)) events.Add(item);
        Assert.NotEmpty(events);
        Assert.Equal(2, speechCalls);
    }

    [Fact]
    public void Header_and_dependency_injection_register_provider()
    {
        Assert.Equal("X-XVoice-Key", HeaderApiKeyResolver.SupportedProviderHeaders["xvoice"]);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-XVoice-Key"] = "xvoice-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("xvoice-key", resolver.Resolve("xvoice"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddProviders();
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("xvoice"));
        Assert.IsType<XVoiceProvider>(registry.GetModelProvider(scope.ServiceProvider, "xvoice"));
    }

    private const string Models = """
        {"object":"list","data":[
          {"id":"cadence-tts","type":"tts","name":"Cadence TTS","created":123,"owned_by":"xvoice",
           "description":"Speech.","capabilities":["batch","streaming"],"supported_languages":["en","es"],
           "status":"available","public_version":"2026-09-17",
           "speech_options":{"response_formats":["wav","mp3","pcm"],"streamable_formats":["wav","mp3","pcm"],"speed":{"min":1,"max":1},"instructions":false}},
          {"id":"cadence-stt","type":"stt","name":"Cadence STT","capabilities":["batch","realtime"],
           "supported_languages":["de","en","es"],"status":"deprecated","speech_options":null}
        ]}
        """;
    private const string Voices = """
        {"object":"list","data":[{"id":"clara","name":"Clara","locale":"en-US","languages":["en","es"],
          "gender":"female","status":"available","preview_url":null,"metadata":{"custom":"sample"}}]}
        """;
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private static XVoiceProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Resolver? resolver = null)
        => new(resolver ?? new Resolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Factory(new(new Handler(send))));
    private sealed class Resolver : IApiKeyResolver
    {
        public string? Key { get; set; } = "key";
        public string? Resolve(string provider) => Key;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
