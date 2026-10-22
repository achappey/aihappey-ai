using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIProviderDecisionCostingTests
{
    [Theory]
    [InlineData("gpt-6-luna", 1000, 0)]
    [InlineData("openai/gpt-6-luna", 1000, 300)]
    [InlineData("gpt-6-luna", 1, 0)]
    [InlineData("gpt-6-luna", 0, 0)]
    [InlineData("gpt-6-luna", 0, 300)]
    public async Task Sdk_decisions_add_cost_and_preserve_upstream_response(string model, int inputTokens, int outputTokens)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var raw = DecisionJson("gpt-6-luna", inputTokens, outputTokens);
        using var client = new HttpClient(new ResponseHandler(async (http, ct) =>
        {
            Assert.Equal("/v1/decisions", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-key", http.Headers.Authorization!.ToString());
            using var payload = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("gpt-6-luna", payload.RootElement.GetProperty("model").GetString());
            var response = JsonResponse(raw);
            response.Headers.Add("x-request-id", "decision-request");
            return response;
        }));
        var provider = CreateProvider(client, cache);

        var result = await provider.DecisionRequestAsync(Request(model));

        var serialized = JsonSerializer.SerializeToElement(result);
        Assert.Equal(inputTokens * 0.0000001m,
            serialized.GetProperty("providerMetadata").GetProperty("gateway").GetProperty("cost").GetDecimal());
        Assert.Equal(raw, result.ProviderMetadata!["openai"].GetRawText());
        Assert.Equal(raw, Assert.IsType<JsonElement>(result.Response!.Body).GetRawText());
        Assert.Equal("openai/gpt-6-luna", result.Response.ModelId);
        Assert.Equal("decision-request", result.Response.Headers!["x-request-id"]);
        Assert.NotNull(result.Response.Timestamp);
        Assert.Equal(inputTokens, result.Usage!.InputTokens);
        Assert.Equal(outputTokens, result.Usage.OutputTokens);
        Assert.Equal(0.75, Assert.IsType<DecisionBooleanAnswer>(result.Answers["flag"]).Probability);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("unknown-model", "unknown-model")]
    [InlineData("gpt-6-luna", "unknown-model")]
    [InlineData("not-gpt-6-luna", "not-gpt-6-luna")]
    public async Task Sdk_decisions_do_not_invent_pricing_for_unknown_response_models(string requestModel, string responseModel)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new ResponseHandler((_, _) =>
            Task.FromResult(JsonResponse(DecisionJson(responseModel, 1000, 100)))));

        var result = await CreateProvider(client, cache).DecisionRequestAsync(Request(requestModel));

        Assert.False(result.ProviderMetadata!.ContainsKey("gateway"));
        Assert.True(result.ProviderMetadata.ContainsKey("openai"));
    }

    [Fact]
    public async Task Native_decisions_remain_unenriched()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new ResponseHandler((_, _) =>
            Task.FromResult(JsonResponse(DecisionJson("gpt-6-luna", 1000, 100)))));

        var result = await CreateProvider(client, cache).OpenAIDecisionRequestAsync(new OpenAIDecisionRequest
        {
            Model = "gpt-6-luna", Input = "evidence",
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "flag", Instructions = "allowed?" }]
        });

        var serialized = JsonSerializer.SerializeToElement(result);
        Assert.False(serialized.TryGetProperty("metadata", out _));
        Assert.False(serialized.TryGetProperty("providerMetadata", out _));
        Assert.False(serialized.TryGetProperty("gateway", out _));
        Assert.True(result.AdditionalProperties!["future"].GetProperty("preserved").GetBoolean());
        Assert.Equal(1000, result.Usage.InputTokens);
        Assert.Equal(100, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task Model_listing_retains_distinct_decision_and_language_pricing()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new ResponseHandler((http, _) => Task.FromResult(JsonResponse(
            http.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => """{"data":[{"id":"gpt-6-luna","owned_by":"openai"}]}""",
                "/v1/decisions" => DecisionJson("gpt-6-luna", 1000, 100),
                _ => """{"data":[],"has_more":false}"""
            }))));
        var provider = CreateProvider(client, cache);

        var models = (await provider.ListModels()).ToList();

        var decision = Assert.Single(models, model => model.Type == "decision");
        var language = Assert.Single(models, model => model.Type != "decision");
        Assert.Equal(0.0000001m, decision.Pricing!.Input);
        Assert.Equal(0m, decision.Pricing.Output);
        Assert.Equal(0.0000005m, language.Pricing!.Output);
        var result = await provider.DecisionRequestAsync(Request("gpt-6-luna"));
        Assert.Equal(1000 * decision.Pricing.Input + 100 * decision.Pricing.Output,
            result.ProviderMetadata!["gateway"].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task Sdk_decisions_preserve_cancellation_and_upstream_errors()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sends = 0;
        using var client = new HttpClient(new ResponseHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("quota exhausted") });
        }));
        var provider = CreateProvider(client, cache);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.DecisionRequestAsync(Request("gpt-6-luna"), cancellation.Token));
        Assert.Equal(0, sends);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DecisionRequestAsync(Request("gpt-6-luna")));
        Assert.Contains("429", error.Message);
        Assert.Contains("quota exhausted", error.Message);
    }

    private static DecisionRequest Request(string model) => new()
    {
        Model = model, State = "evidence",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "allowed?" } }
    };

    private static string DecisionJson(string model, int inputTokens, int outputTokens)
        => JsonSerializer.Serialize(new
        {
            model,
            answers = new[] { new { name = "flag", type = "predicate", probability = 0.75 } },
            usage = new
            {
                input_tokens = inputTokens, output_tokens = outputTokens, total_tokens = inputTokens + outputTokens,
                input_tokens_details = new { cached_tokens = inputTokens / 2, cache_write_tokens = inputTokens / 4 }
            },
            future = new { preserved = true }
        });

    private static OpenAIProvider CreateProvider(HttpClient client, IMemoryCache cache)
        => new(new KeyResolver(), new ClientFactory(client), new AsyncCacheHelper(cache), new EndUserIdResolver());

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class EndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
