using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Fireworks;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Fireworks;

public sealed class FireworksProviderDecisionTests
{
    [Fact]
    public async Task OpenAI_contract_posts_decision_questions_to_Fireworks_endpoint()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/inference/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);

            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("accounts/fireworks/models/rune", body.GetProperty("model").GetString());
            Assert.Equal("Customer requests a refund.", body.GetProperty("input").GetString());
            Assert.Equal("predicate", body.GetProperty("questions")[0].GetProperty("type").GetString());
            Assert.Equal("choice", body.GetProperty("questions")[1].GetProperty("type").GetString());
            Assert.Equal("score", body.GetProperty("questions")[2].GetProperty("type").GetString());

            return JsonResponse("""
                {
                  "model":"accounts/fireworks/models/rune",
                  "answers":[
                    {"type":"predicate","name":"refund","probability":0.98,"confidence":0.96},
                    {"type":"choice","name":"tone","choice":"angry",
                      "probabilities":[{"value":"angry","probability":0.9},{"value":"neutral","probability":0.1}],
                      "confidence":0.8},
                    {"type":"score","name":"urgency","score":1.25,
                      "probabilities":[{"value":0,"label":"low","probability":0.75},{"value":1,"label":"high","probability":0.25}],
                      "confidence":0.5}
                  ],
                  "usage":{"input_tokens":32,"output_tokens":0,"total_tokens":32}
                }
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "accounts/fireworks/models/rune",
            Input = new("Customer requests a refund."),
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "refund", Instructions = "Is a refund requested?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "tone",
                    Instructions = "What is the customer's tone?",
                    Choices = [new() { Value = "angry" }, new() { Value = "neutral" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "urgency",
                    Instructions = "How urgent is this?",
                    Levels = [new() { Label = "low" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal(3, result.Answers.Count);
        Assert.Equal(0.98, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        Assert.Equal("angry", Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]).Choice.Value.GetString());
        Assert.Equal(1.25, Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]).Score);
    }

    [Fact]
    public async Task Vercel_contract_uses_shared_mapping_and_preserves_provider_metadata()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            Assert.Equal("/inference/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("trace", Assert.Single(request.Headers.GetValues("x-fireworks-trace")));

            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("accounts/fireworks/models/rune", body.GetProperty("model").GetString());
            Assert.Equal("Customer requests a refund.", body.GetProperty("input").GetString());
            Assert.Equal("predicate", body.GetProperty("questions")[0].GetProperty("type").GetString());
            Assert.Equal("refund", body.GetProperty("questions")[0].GetProperty("name").GetString());

            return JsonResponse("""
                {
                  "model":"accounts/fireworks/models/rune",
                  "answers":[{"type":"predicate","name":"refund","probability":0.98,"confidence":0.96}],
                  "usage":{"input_tokens":12,"output_tokens":0,"total_tokens":12},
                  "future_field":"preserved"
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "fireworks/accounts/fireworks/models/rune",
            State = JsonSerializer.SerializeToElement("Customer requests a refund."),
            Questions = new()
            {
                ["refund"] = new DecisionBooleanQuestion { Instructions = "Is a refund requested?" }
            },
            Headers = new() { ["x-fireworks-trace"] = "trace" }
        });

        Assert.Equal(0.98, Assert.IsType<DecisionBooleanAnswer>(result.Answers["refund"]).Probability);
        Assert.Equal(12, result.Usage!.InputTokens);
        Assert.Equal("fireworks/accounts/fireworks/models/rune", result.Response!.ModelId);
        Assert.True(result.ProviderMetadata!["fireworks"].TryGetProperty("future_field", out _));
    }

    private static FireworksProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

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

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
