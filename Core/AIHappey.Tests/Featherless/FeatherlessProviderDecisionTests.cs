using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Featherless;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Featherless;

public sealed class FeatherlessProviderDecisionTests
{
    [Fact]
    public async Task DecisionRequest_sends_classifier_contract_and_maps_all_answer_types()
    {
        JsonElement? captured = null;
        var provider = CreateProvider(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/classifier", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            captured = document.RootElement.Clone();
            return Json("""
                {
                  "model":"featherless-ai/Qwen3.6-35B-A3B-classifier",
                  "answers":{
                    "sentiment":{"type":"choice","choice":"positive","confidence":0.94,"probabilities":{"positive":0.94,"negative":0.06}},
                    "quality":{"type":"score","score":4,"confidence":0.81,"probabilities":{"0":0.01,"1":0.02,"2":0.05,"3":0.11,"4":0.81},
                      "legend":{"0":"Very poor","1":"Poor","2":"Fair","3":"Good","4":"Excellent"}},
                    "dog":{"type":"noul","noul":0.97}
                  },
                  "usage":{"input_tokens":407,"output_tokens":3,"cost":0.000004}
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "featherless/featherless-ai/Qwen3.6-35B-A3B-classifier",
            State = "Review text",
            Questions = new()
            {
                ["sentiment"] = new DecisionChoiceQuestion
                {
                    Instructions = "Overall sentiment.",
                    Criteria = new() { ["positive"] = "Favorable", ["negative"] = "Unfavorable" }
                },
                ["quality"] = new DecisionScoreQuestion
                {
                    Instructions = "Rate quality.",
                    Criteria = ["Very poor", "Poor", "Fair", "Good", "Excellent"]
                },
                ["dog"] = new DecisionBooleanQuestion { Instructions = "Does it mention a dog?" }
            }
        });

        Assert.NotNull(captured);
        Assert.Equal("featherless-ai/Qwen3.6-35B-A3B-classifier", captured.Value.GetProperty("model").GetString());
        Assert.Equal("Review text", captured.Value.GetProperty("state").GetString());
        Assert.Equal("noul", captured.Value.GetProperty("questions").GetProperty("dog").GetProperty("type").GetString());
        Assert.Equal("choice", captured.Value.GetProperty("questions").GetProperty("sentiment").GetProperty("type").GetString());
        Assert.Equal(0.97, Assert.IsType<DecisionBooleanAnswer>(result.Answers["dog"]).Probability);
        Assert.Equal(4, Assert.IsType<DecisionScoreAnswer>(result.Answers["quality"]).Score);
        Assert.Equal("positive", Assert.IsType<DecisionChoiceAnswer>(result.Answers["sentiment"]).Choice);
        Assert.Equal(407, result.Usage!.InputTokens);
        Assert.Equal("featherless/featherless-ai/Qwen3.6-35B-A3B-classifier", result.Response!.ModelId);
        Assert.Equal(0.000004, result.ProviderMetadata!["featherless"].GetProperty("usage").GetProperty("cost").GetDouble());
    }

    [Fact]
    public async Task OpenAIDecisionRequest_maps_values_and_messages_to_classifier_contract()
    {
        JsonElement? captured = null;
        var provider = CreateProvider(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            captured = document.RootElement.Clone();
            return Json("""
                {"model":"featherless-ai/classifier","answers":{
                  "question-0":{"type":"choice","choice":"option-1","confidence":0.8,
                    "probabilities":{"option-0":0.2,"option-1":0.8}},
                  "question-1":{"type":"noul","noul":0.7}},
                 "usage":{"input_tokens":12,"output_tokens":0}}
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "featherless/classifier",
            Input = new OpenAIDecisionInput(new[]
            {
                new OpenAIDecisionInputMessage { Content = "A review to classify." }
            }),
            Questions =
            [
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "sentiment",
                    Instructions = "What is the sentiment?",
                    Choices =
                    [
                        new() { Value = "negative" },
                        new() { Value = "positive", Description = "Favorable" }
                    ]
                },
                new OpenAIDecisionPredicateQuestion { Name = "safe", Instructions = "Is it safe?" }
            ]
        });

        Assert.NotNull(captured);
        Assert.Equal("A review to classify.",
            captured.Value.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("option-1",
            captured.Value.GetProperty("questions").GetProperty("question-0").GetProperty("criteria").EnumerateObject().Last().Name);
        Assert.Equal("positive", Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[0]).Choice.Value.GetString());
        Assert.Equal("sentiment", result.Answers[0].Name);
        Assert.Equal(12, result.Usage.InputTokens);
        Assert.Equal(12, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task DecisionRequest_rejects_classifier_criteria_outside_documented_bounds()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No request expected."));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(new()
        {
            Model = "classifier",
            State = "context",
            Questions = new()
            {
                ["choice"] = new DecisionChoiceQuestion
                {
                    Instructions = "Pick one.",
                    Criteria = new() { ["only"] = "one" }
                }
            }
        }));
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static FeatherlessProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new Handler(responder))
        {
            BaseAddress = new Uri("https://api.featherless.ai/")
        };
        return new FeatherlessProvider(
            new KeyResolver(),
            new ClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    }

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "featherless" ? "test-key" : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
