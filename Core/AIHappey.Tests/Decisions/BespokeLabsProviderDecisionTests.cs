using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.BespokeLabs;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Decisions;

public sealed class BespokeLabsProviderDecisionTests
{
    [Fact]
    public async Task Decision_contract_maps_all_question_types_usage_and_metadata()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            Assert.Equal("/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-bespokelabs-trace")));
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("nimble-v4-max", root.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("state").ValueKind);
            var questions = root.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("boolean").GetProperty("type").GetString());
            Assert.False(questions.GetProperty("boolean").TryGetProperty("instructions", out _));
            Assert.Equal("choice", questions.GetProperty("route").GetProperty("type").GetString());
            Assert.False(questions.GetProperty("route").TryGetProperty("instructions", out _));
            Assert.Equal(JsonValueKind.Null,
                questions.GetProperty("route").GetProperty("criteria").GetProperty("option-0").ValueKind);
            Assert.Equal("score", questions.GetProperty("urgency").GetProperty("type").GetString());
            Assert.False(questions.GetProperty("urgency").TryGetProperty("instructions", out _));
            Assert.Equal(2, questions.GetProperty("urgency").GetProperty("criteria").GetArrayLength());
            return Response("nimble-v4-max", """
                {
                  "boolean":{"type":"noul","noul":0.9},
                  "route":{"type":"choice","choice":"option-1","probabilities":{"option-0":0.2,"option-1":0.8},"confidence":0.8},
                  "urgency":{"type":"score","score":0.75,"legend":{"0":"Routine","1":"Outage"},
                    "probabilities":{"0":0.25,"1":0.75},"confidence":0.75}
                }
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "bespokelabs/nimble-v4-max",
            State = JsonSerializer.SerializeToElement(new { ticket = "refund" }),
            Headers = new()
            {
                ["Authorization"] = "attacker",
                ["x-bespokelabs-key"] = "attacker",
                ["x-bespokelabs-trace"] = "trace"
            },
            ProviderOptions = new() { ["bespokelabs"] = JsonSerializer.SerializeToElement(new { }) },
            Questions = new()
            {
                ["boolean"] = new DecisionBooleanQuestion
                {
                    Criteria = new() { True = "yes", False = null }
                },
                ["route"] = new DecisionChoiceQuestion
                {
                    Criteria = new() { ["billing"] = null, ["support"] = "Other questions" }
                },
                ["urgency"] = new DecisionScoreQuestion
                {
                    Criteria = ["Routine", "Outage"]
                }
            }
        });

        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(response.Answers["boolean"]).Probability);
        Assert.Equal("support", Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]).Choice);
        Assert.Equal(0.8, Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]).Probabilities!["support"]);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["urgency"]).Score);
        Assert.Equal(10, response.Usage!.InputTokens);
        Assert.Equal(3, response.Usage.OutputTokens);
        Assert.Equal("bespokelabs/nimble-v4-max", response.Response!.ModelId);
        Assert.Equal("bespoke-request", response.Response.Id);
        Assert.True(response.ProviderMetadata!.ContainsKey("bespokelabs"));
    }

    [Fact]
    public async Task OpenAI_contract_preserves_typed_choices_score_labels_and_alias()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Equal("nimble-v4", root.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Array, root.GetProperty("state").ValueKind);
            var criteria = root.GetProperty("questions").GetProperty("question-1").GetProperty("criteria");
            Assert.Equal(JsonValueKind.Null, criteria.GetProperty("option-0").ValueKind);
            Assert.Equal(JsonValueKind.Null, criteria.GetProperty("option-1").ValueKind);
            return Response("nimble-v4", """
                {
                  "question-0":{"type":"noul","noul":0.7},
                  "question-1":{"type":"choice","choice":"option-1",
                    "probabilities":{"option-0":0.1,"option-1":0.9},"confidence":0.9},
                  "question-2":{"type":"score","score":1.25,"legend":{"0":"low","1":"medium","2":"high"},
                    "probabilities":{"0":0.25,"1":0.25,"2":0.5},"confidence":0.5}
                }
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "bespokelabs/nimble-v4",
            Input = new OpenAIDecisionInputMessage[]
            {
                new() { Content = "one" },
                new() { Content = "two" }
            },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Instructions = "Is this relevant?", Name = "flag" },
                new OpenAIDecisionChoiceQuestion
                {
                    Instructions = "Select the exact value.",
                    Name = "flag",
                    Choices = [new() { Value = "true" }, new() { Value = true }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "Rate it.",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal(0.7, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        Assert.Equal("flag", result.Answers[0].Name);
        Assert.Equal("flag", result.Answers[1].Name);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal(new[] { "low", "medium", "high" },
            Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]).Probabilities.Select(item => item.Label));
        Assert.Equal(13, result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("nimble-latest")]
    [InlineData("nimble-v4-max")]
    [InlineData("nimble-v4")]
    [InlineData("nimble-v3")]
    public async Task Supports_each_documented_model_alias(string model)
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
            return Response(model, """{"question-0":{"type":"noul","noul":0.5}}""");
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = model,
            Input = "state",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "true?", Name = "flag" }]
        });

        Assert.Equal(model, result.Model);
    }

    [Fact]
    public async Task Falls_back_to_the_documented_alternate_endpoint_on_not_found()
    {
        var attempts = 0;
        var provider = CreateProvider((http, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                Assert.Equal("/v1/systemone", http.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            Assert.Equal("/v1/nimble/systemone", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Response("nimble-v4-max", """{"question-0":{"type":"noul","noul":0.5}}"""));
        });

        await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "nimble-v4-max",
            Input = "state",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "true?", Name = "flag" }]
        });

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Rejects_unsupported_models_and_provider_options_before_sending()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Request must not be sent."));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "nimble-v5",
            Input = "state",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "true?" }]
        }));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(new()
        {
            Model = "nimble-v4-max",
            State = "state",
            ProviderOptions = new() { ["bespokelabs"] = JsonSerializer.SerializeToElement(new { unknown = true }) },
            Questions = new()
            {
                ["flag"] = new DecisionBooleanQuestion { Instructions = "true?" }
            }
        }));
    }

    [Fact]
    public async Task Rejects_question_count_and_body_size_limits_before_sending()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Request must not be sent."));
        var tooManyQuestions = Enumerable.Range(0, 65)
            .Select(index => (OpenAIDecisionQuestion)new OpenAIDecisionPredicateQuestion
            {
                Instructions = "Question?",
                Name = $"q-{index}"
            })
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "nimble-v4-max",
            Input = "state",
            Questions = tooManyQuestions
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "nimble-v4-max",
            Input = new string('a', 1_048_600),
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Question?" }]
        }));
    }

    [Fact]
    public async Task Model_catalog_lists_only_documented_models_as_decisions()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Model listing must not make a request."));
        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal(4, models.Count);
        Assert.All(models.Values, model => Assert.Equal("decision", model.Type));
        Assert.Contains("bespokelabs/nimble-v4-max", models.Keys);
        Assert.Contains("bespokelabs/nimble-v4", models.Keys);
        Assert.Contains("bespokelabs/nimble-v3", models.Keys);
        Assert.Contains("bespokelabs/nimble-latest", models.Keys);
    }

    [Fact]
    public async Task Preserves_upstream_http_errors_and_honors_cancellation()
    {
        var failing = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"detail\":\"busy\"}")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => failing.OpenAIDecisionRequestAsync(PredicateRequest()));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("busy", error.Message);

        var noSend = CreateProvider((_, _) => throw new InvalidOperationException("Request must not be sent."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => noSend.OpenAIDecisionRequestAsync(PredicateRequest(), cancellation.Token));
    }

    private static OpenAIDecisionRequest PredicateRequest() => new()
    {
        Model = "nimble-v4-max",
        Input = "state",
        Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "true?", Name = "flag" }]
    };

    private static HttpResponseMessage Response(string model, string answers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"model\":\"" + model + "\",\"answers\":" + answers
                + ",\"usage\":{\"input_tokens\":10,\"output_tokens\":3}}",
                Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "bespoke-request");
        return response;
    }

    private static BespokeLabsProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))));

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
