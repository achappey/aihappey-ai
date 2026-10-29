using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Mistral;

public sealed class MistralRealtimeTests
{
    private const string Model = "voxtral-mini-transcribe-realtime-3";
    private const string Success = """{"client_secret":{"value":"rt_fixture","expires_at":"2099-01-01T00:00:00Z"},"expires_at":"2099-02-01T00:00:00Z","purpose":"realtime"}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_contracts_mint_model_scoped_tokens_and_use_secret_expiry(bool generic)
    {
        var calls = 0;
        var provider = Create(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/client/sessions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("server-only-key", request.Headers.Authorization.Parameter);
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Assert.Equal(Model, payload.GetProperty("model").GetString());
            Assert.Equal("realtime", payload.GetProperty("purpose").GetString());
            Assert.Equal(120, payload.GetProperty("ttl_seconds").GetInt32());
            Assert.Equal(3, payload.EnumerateObject().Count());
            return Json(Success);
        });
        var options = new Dictionary<string, JsonElement> { ["mistral"] = JsonSerializer.SerializeToElement(new { ttl_seconds = 120, purpose = "other", model = "wrong", language = "nl" }) };
        if (generic)
        {
            var response = await provider.CreateRealtimeTokenAsync("mistral/" + Model, new() { SessionConfig = new() { ProviderOptions = options } });
            Assert.Equal("rt_fixture", response.Token);
            Assert.Equal("wss://api.mistral.ai/v1/audio/transcriptions/realtime?model=" + Model, response.Url);
            Assert.Equal(DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeSeconds(), response.ExpiresAt);
        }
        else
        {
            var response = await provider.GetRealtimeToken(new() { Model = "mistral/" + Model, ProviderOptions = options });
            Assert.Equal("rt_fixture", response.Value);
            Assert.Equal(DateTimeOffset.Parse("2099-01-01T00:00:00Z").ToUnixTimeSeconds(), response.ExpiresAt);
            Assert.Null(response.ProviderMetadata);
        }
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"120\"")]
    public async Task Invalid_ttl_is_rejected_before_network(string ttl)
    {
        var provider = Create((_, _) => throw new Exception("Unexpected network call"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(new() { Model = Model,
            ProviderOptions = new() { ["mistral"] = JsonDocument.Parse("{\"ttl_seconds\":" + ttl + "}").RootElement.Clone() } }));
    }

    [Theory]
    [InlineData("mistral-small-latest")]
    [InlineData("voxtral-mini-transcribe-3")]
    [InlineData("voxtral-mini-tts-3-0")]
    [InlineData("voxtral-mini-transcribe-realtime-unknown")]
    public async Task Unsupported_models_are_not_minted(string model)
    {
        var provider = Create((_, _) => throw new Exception("Unexpected network call"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(new() { Model = model }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"client_secret\":{\"value\":\"server-only-key\",\"expires_at\":\"2099-01-01T00:00:00Z\"}}")]
    [InlineData("{\"client_secret\":{\"value\":\"rt_fixture\",\"expires_at\":\"2000-01-01T00:00:00Z\"}}")]
    [InlineData("{\"client_secret\":{\"value\":\"rt_fixture\",\"expires_at\":\"invalid\"}}")]
    public async Task Invalid_tokens_are_rejected_without_disclosing_body(string body)
    {
        var provider = Create((_, _) => Task.FromResult(Json(body)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(new() { Model = Model }));
        Assert.DoesNotContain("server-only-key", error.ToString());
        Assert.DoesNotContain("rt_fixture", error.ToString());
    }

    [Fact]
    public async Task Default_ttl_is_provider_owned_and_top_level_expiry_is_supported()
    {
        var provider = Create(async (request, ct) =>
        {
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Assert.False(payload.TryGetProperty("ttl_seconds", out _));
            return Json("""{"client_secret":{"value":"rt_fixture"},"expires_at":"2099-01-01T00:00:00Z"}""");
        });
        Assert.Equal("rt_fixture", (await provider.GetRealtimeToken(new() { Model = Model })).Value);
    }

    [Fact]
    public async Task Upstream_errors_are_credential_safe_and_cancellation_propagates()
    {
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("server-only-key rt_secret") }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(new() { Model = Model }));
        Assert.Contains("403", error.Message); Assert.DoesNotContain("rt_secret", error.ToString());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRealtimeToken(new() { Model = Model }, cancellation.Token));
    }

    [Fact]
    public async Task Catalog_only_marks_supported_realtime_transcription_models()
    {
        string[] ids = [Model, "voxtral-mini-transcribe-realtime-3-0", "voxtral-mini-transcribe-realtime-2602", "voxtral-mini-transcribe-3", "voxtral-mini-tts-3-0", "mistral-small-latest", "voxtral-small-latest"];
        var provider = Create((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => Json(JsonSerializer.Serialize(new { data = ids.Select(id => new { id }) })),
            "/v1/agents" => Json("[]"),
            _ => Json("{\"items\":[],\"total\":0}")
        }));
        var models = (await provider.ListModels()).ToArray();
        foreach (var id in ids)
        {
            var model = Assert.Single(models, model => model.Id == "mistral/" + id && model.Type != "image");
            if (id.Contains("realtime")) { Assert.Equal("transcription", model.Type); Assert.Contains("real-time", model.Tags!); Assert.Contains("realtime-token", model.Tags!); }
            else Assert.DoesNotContain("real-time", model.Tags ?? []);
        }
        Assert.Equal("transcription", models.Single(m => m.Id == "mistral/voxtral-mini-transcribe-3").Type);
        Assert.Equal("language", models.Single(m => m.Id == "mistral/voxtral-small-latest").Type);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static MistralProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Factory(new HttpClient(new Handler(respond))));
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "server-only-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return respond(request, ct); } }
}
