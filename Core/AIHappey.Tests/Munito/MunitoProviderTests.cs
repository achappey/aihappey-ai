using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Munito;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Munito;

public sealed class MunitoProviderTests
{
    [Fact]
    public async Task Chat_responses_and_messages_use_documented_routes()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider("munito-key", (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/models"))
                return Task.FromResult(Json("{\"data\":[]}"));
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("err") });
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new() { Model = "deepseek-v4-flash", Messages = [] }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResponsesAsync(new() { Model = "deepseek-v4-flash", Input = "hi" }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.MessagesAsync(new() { Model = "kolibri-1", MaxTokens = 16, Messages = [] }, []));

        Assert.Equal(
            new[] { "/inference/v1/chat/completions", "/inference/v1/responses", "/inference/v1/messages" },
            requests.Select(r => r.RequestUri!.AbsolutePath));
        Assert.All(requests, r => Assert.Equal("munito-key", r.Headers.Authorization!.Parameter));
        Assert.Equal("2023-06-01", Assert.Single(requests[2].Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public async Task Decision_maps_to_systemone_and_response()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var provider = CreateProvider("k", async (request, _) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json("""{"model":"perplexity-ai/pplx-decider-v1.2","answers":{"retry":{"type":"noul","noul":0.96}},"usage":{"input_tokens":100,"output_tokens":0}}""");
        });

        var response = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "perplexity-ai/pplx-decider-v1.2",
            State = "The payment failed.",
            Questions = new() { ["retry"] = new DecisionBooleanQuestion { Instructions = "Did the payment fail?" } }
        });

        Assert.Equal("/inference/v1/systemone", captured!.RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("noul", doc.RootElement.GetProperty("questions").GetProperty("retry").GetProperty("type").GetString());
        var answer = Assert.IsType<DecisionBooleanAnswer>(response.Answers["retry"]);
        Assert.Equal(0.96, answer.Probability, 3);
        Assert.Equal(100, response.Usage!.InputTokens);
    }

    [Fact]
    public async Task Decision_eu_region_uses_eu_host()
    {
        Uri? uri = null;
        var provider = CreateProvider("k", (request, _) =>
        {
            uri = request.RequestUri;
            return Task.FromResult(Json("""{"answers":{"a":{"type":"noul","noul":0.5}},"usage":{"input_tokens":1,"output_tokens":0}}"""));
        });

        await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "blockbrain/cygnet",
            State = "x",
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } },
            ProviderOptions = new() { ["munito"] = JsonDocument.Parse("""{"region":"eu"}""").RootElement.Clone() }
        });

        Assert.Equal("api.eu.munito.ai", uri!.Host);
    }

    [Fact]
    public async Task Decision_rate_limit_surfaces_error()
    {
        var provider = CreateProvider("k", (_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"detail\":\"slow\"}") }));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "blockbrain/cygnet",
            State = "x",
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
    }

    [Fact]
    public async Task Decision_rejects_too_many_questions_and_unsupported_options()
    {
        var provider = CreateProvider("k", (_, _) => throw new InvalidOperationException("must not call"));
        var questions = Enumerable.Range(0, 65).ToDictionary(i => $"q{i}", _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "q" });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "blockbrain/cygnet", State = "x", Questions = questions
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "blockbrain/cygnet", State = "x", MaxOutputTokens = 5,
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
    }

    [Fact]
    public async Task Ocr_models_are_special_and_unsupported_capabilities_throw()
    {
        var provider = CreateProvider("k", (_, _) => throw new InvalidOperationException("no http"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new() { Model = "x", Prompt = "p" }, default));
    }

    [Fact]
    public async Task ListModels_without_key_is_empty()
    {
        var provider = CreateProvider(null, (_, _) => throw new InvalidOperationException("no http"));
        Assert.Empty(await provider.ListModels());
    }

    [Fact]
    public async Task ListModels_applies_catalog_types()
    {
        var provider = CreateProvider("k", (request, _) =>
        {
            Assert.Equal("/inference/v1/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"data":[{"id":"blockbrain/cygnet","object":"model","created":1,"owned_by":"x"},{"id":"lightonai/lightonocr-2-1b","object":"model","created":1,"owned_by":"x"}]}"""));
        });

        var models = (await provider.ListModels()).ToList();
        Assert.Equal("decision", models.Single(m => m.Id.EndsWith("blockbrain/cygnet")).Type);
        Assert.Contains(models, m => m.Id.EndsWith("ocr-auto"));
        Assert.Contains(models, m => m.Type == "transcription");
    }

    [Fact]
    public void Provider_is_registered_with_header_mapping()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Munito-Key"] = " key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("key", resolver.Resolve("MUNITO"));

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, d => d.ImplementationType == typeof(MunitoProvider));
        using var root = services.BuildServiceProvider();
        Assert.True(root.GetRequiredService<ProviderRegistry>().HasModelProvider("munito"));
    }

    private static MunitoProvider CreateProvider(string? key, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(key),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "munito" ? key : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
    }
}
