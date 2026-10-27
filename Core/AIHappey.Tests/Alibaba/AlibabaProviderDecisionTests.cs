using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Alibaba;

public sealed class AlibabaProviderDecisionTests
{
    [Fact]
    public async Task Decision_contract_sends_system_one_questions_and_preserves_native_metadata()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("/compatible-mode/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-alibaba-trace")));
            Assert.False(http.Headers.Contains("x-alibaba-key"));
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("decision-model-preview", root.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("state").ValueKind);
            var questions = root.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("escalate").GetProperty("type").GetString());
            Assert.Equal("yes", questions.GetProperty("escalate").GetProperty("criteria").GetProperty("true").GetString());
            Assert.Equal("choice", questions.GetProperty("department").GetProperty("type").GetString());
            Assert.Equal("Payments", questions.GetProperty("department").GetProperty("criteria").GetProperty("billing").GetString());
            Assert.Equal(4, questions.GetProperty("severity").GetProperty("criteria").GetArrayLength());
            return DecisionResponse("""
                {
                  "model":"decision-model-preview",
                  "request_id":"native-request-id",
                  "answers":{
                    "department":{"type":"choice","choice":"billing","confidence":0.94,
                      "probabilities":{"billing":0.94,"technical":0.06}},
                    "escalate":{"type":"noul","noul":0.99},
                    "severity":{"type":"score","score":2.25,"confidence":0.91,
                      "legend":{"0":"minor","1":"some impact","2":"core unavailable","3":"severe"},
                      "probabilities":{"0":0.0,"1":0.01,"2":0.73,"3":0.26}}
                  },
                  "usage":{"input_tokens":125},
                  "latency_ms":52.9
                }
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "alibaba/decision-model-preview",
            State = JsonSerializer.SerializeToElement(new { ticket = "T-1001", content = "Payment is delayed." }),
            Headers = new() { ["Authorization"] = "attacker", ["x-alibaba-key"] = "attacker", ["x-alibaba-trace"] = "trace" },
            ProviderOptions = new() { ["alibaba"] = JsonSerializer.SerializeToElement(new { }) },
            Questions = new()
            {
                ["department"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle this?",
                    Criteria = new() { ["billing"] = "Payments", ["technical"] = "Product failures" }
                },
                ["escalate"] = new DecisionBooleanQuestion
                {
                    Instructions = "Notify on-call?",
                    Criteria = new() { True = "yes", False = "no" }
                },
                ["severity"] = new DecisionScoreQuestion
                {
                    Instructions = "How severe is the issue?",
                    Criteria = ["minor", "some impact", "core unavailable", "severe"]
                }
            }
        });

        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(response.Answers["department"]).Choice);
        Assert.Equal(0.94, Assert.IsType<DecisionChoiceAnswer>(response.Answers["department"]).Probabilities!["billing"]);
        Assert.Equal(0.99, Assert.IsType<DecisionBooleanAnswer>(response.Answers["escalate"]).Probability);
        Assert.Equal(2.25, Assert.IsType<DecisionScoreAnswer>(response.Answers["severity"]).Score);
        Assert.Equal(125, response.Usage!.InputTokens);
        Assert.Null(response.Usage.OutputTokens);
        Assert.Equal("alibaba/decision-model-preview", response.Response!.ModelId);
        Assert.Equal("native-request-id", response.Response.Id);
        Assert.True(response.ProviderMetadata!["alibaba"].TryGetProperty("latency_ms", out _));
    }

    [Fact]
    public async Task OpenAI_contract_preserves_question_names_choice_values_and_score_labels()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("decision-model-preview", root.GetProperty("model").GetString());
            Assert.Equal("text evidence", root.GetProperty("state").GetProperty("messages")[0].GetProperty("content").GetString());
            var questions = root.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("question-0").GetProperty("type").GetString());
            var criteria = questions.GetProperty("question-1").GetProperty("criteria");
            Assert.Equal("billing: Payment issues", criteria.GetProperty("option-0").GetString());
            Assert.Equal("true: Escalate", criteria.GetProperty("option-1").GetString());
            Assert.Equal("high: Severe", questions.GetProperty("question-2").GetProperty("criteria")[2].GetString());
            return DecisionResponse("""
                {
                  "model":"decision-model-preview",
                  "answers":{
                    "question-0":{"type":"noul","noul":0.7},
                    "question-1":{"type":"choice","choice":"option-1","confidence":0.8,
                      "probabilities":{"option-0":0.2,"option-1":0.8}},
                    "question-2":{"type":"score","score":1.25,"confidence":0.5,
                      "legend":{"0":"low: Minor","1":"medium","2":"high: Severe"},
                      "probabilities":{"0":0.25,"1":0.25,"2":0.5}}
                  },
                  "usage":{"input_tokens":16}
                }
                """);
        });

        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "alibaba/decision-model-preview",
            Input = new OpenAIDecisionInputMessage[] { new() { Content = "text evidence" } },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "flag", Instructions = "Is this urgent?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route",
                    Instructions = "Where should this go?",
                    Choices =
                    [
                        new() { Value = "billing", Description = "Payment issues" },
                        new() { Value = true, Description = "Escalate" }
                    ]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "severity",
                    Instructions = "How severe?",
                    Levels = [new() { Label = "low", Description = "Minor" }, new() { Label = "medium" }, new() { Label = "high", Description = "Severe" }]
                }
            ]
        });

        Assert.Equal(0.7, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(new string?[] { "flag", "route", "severity" }, response.Answers.Select(answer => answer.Name));
        Assert.Equal(0.8, choice.Confidence);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]);
        Assert.Equal(1.25, score.Score);
        Assert.Equal(new[] { "low", "medium", "high" }, score.Probabilities.Select(item => item.Label));
        Assert.Equal(16, response.Usage.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.True(response.AdditionalProperties!["providerMetadata"].GetProperty("alibaba").TryGetProperty("answers", out _));
    }

    [Fact]
    public async Task Decision_contract_preserves_string_and_array_states()
    {
        var states = new[]
        {
            JsonSerializer.SerializeToElement("ticket evidence"),
            JsonSerializer.SerializeToElement(new[] { "first evidence", "second evidence" })
        };
        foreach (var state in states)
        {
            var provider = CreateProvider(async (http, ct) =>
            {
                using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal(state.ValueKind, body.RootElement.GetProperty("state").ValueKind);
                Assert.Equal(state.GetRawText(), body.RootElement.GetProperty("state").GetRawText());
                return DecisionResponse("""
                    {"model":"decision-model-preview","answers":{"flag":{"type":"noul","noul":0.5}},
                     "usage":{"input_tokens":3}}
                    """);
            });
            await provider.DecisionRequestAsync(new()
            {
                Model = "decision-model-preview",
                State = state,
                Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Is it valid?" } }
            });
        }
    }

    [Fact]
    public async Task Decision_contract_enforces_documented_question_limits_before_sending()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Request must not be sent."));
        var request = new DecisionRequest
        {
            Model = "decision-model-preview",
            State = "evidence",
            Questions = Enumerable.Range(0, 17).ToDictionary(
                index => index.ToString(),
                _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "test" })
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request.Questions = new()
        {
            ["score"] = new DecisionScoreQuestion { Instructions = "rate", Criteria = ["only one"] }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Decision_contract_preserves_upstream_error_status_and_body()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("quota exhausted", Encoding.UTF8, "application/json")
        }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.DecisionRequestAsync(new()
            {
                Model = "decision-model-preview",
                State = "evidence",
                Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "test" } }
            }));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("quota exhausted", exception.Message);
    }

    private static HttpResponseMessage DecisionResponse(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "header-request-id");
        return response;
    }

    private static AlibabaProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

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
