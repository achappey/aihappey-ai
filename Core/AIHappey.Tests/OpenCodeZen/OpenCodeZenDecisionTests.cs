using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.OpenCodeZen;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenCodeZen;

public sealed class OpenCodeZenDecisionTests
{
    [Fact]
    public async Task Native_contract_maps_all_question_types_and_preserves_metadata()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://opencode.ai/inference/systemone/v1/systemone", http.RequestUri!.ToString());
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("opencodezen-key", http.Headers.Authorization.Parameter);
            Assert.False(http.Headers.Contains("x-api-key"));
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-opencodezen-trace")));
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal(3, body.EnumerateObject().Count());
            Assert.Equal("jev-1.13", body.GetProperty("model").GetString());
            Assert.Equal("evidence", body.GetProperty("state").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("urgent").GetProperty("type").GetString());
            Assert.False(questions.GetProperty("urgent").TryGetProperty("criteria", out _));
            Assert.Equal("Payments", questions.GetProperty("route").GetProperty("criteria").GetProperty("billing").GetString());
            Assert.Equal("high", questions.GetProperty("severity").GetProperty("criteria")[1].GetString());
            return Response("""
                {"urgent":{"type":"noul","noul":0.92},
                 "route":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"support":0.2},"confidence":0.8},
                 "severity":{"type":"score","score":0.8,"legend":{"0":"low","1":"high"},
                   "probabilities":{"0":0.2,"1":0.8},"confidence":0.8}}
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "opencodezen/jev-1.13",
            State = "evidence",
            Headers = new() { ["Authorization"] = "attacker", ["x-api-key"] = "attacker", ["x-opencodezen-trace"] = "trace" },
            Questions = new()
            {
                ["urgent"] = new DecisionBooleanQuestion { Instructions = "Is it urgent?" },
                ["route"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["billing"] = "Payments", ["support"] = "Other requests" }
                },
                ["severity"] = new DecisionScoreQuestion { Instructions = "Rate severity.", Criteria = ["low", "high"] }
            }
        });

        Assert.Equal(0.92, Assert.IsType<DecisionBooleanAnswer>(result.Answers["urgent"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(0.8, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score);
        Assert.Equal(24, result.Usage!.InputTokens);
        Assert.Equal(0, result.Usage.OutputTokens);
        Assert.Equal("opencodezen/jev-1.13", result.Response!.ModelId);
        Assert.Equal("request-id", result.Response.Id);
        Assert.True(result.ProviderMetadata!.ContainsKey("opencodezen"));
        Assert.Equal(0.8, result.ProviderMetadata["opencodezen"].GetProperty("answers")
            .GetProperty("severity").GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task OpenAI_contract_preserves_choice_values_and_maps_score_scale()
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("evidence", body.GetProperty("state").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal(new[] { "question-0", "question-1", "question-2" },
                questions.EnumerateObject().Select(item => item.Name));
            Assert.Equal("true", questions.GetProperty("question-1").GetProperty("criteria")
                .GetProperty("option-0").GetString());
            Assert.Equal("true", questions.GetProperty("question-1").GetProperty("criteria")
                .GetProperty("option-1").GetString());
            return Response("""
                {"question-0":{"type":"noul","noul":0.9},
                 "question-1":{"type":"choice","choice":"option-1",
                   "probabilities":{"option-0":0.1,"option-1":0.9},"confidence":0.9},
                 "question-2":{"type":"score","score":0.8,"legend":{"0":"low","1":"medium","2":"high"},
                   "probabilities":{"0":0.2,"1":0.8,"2":0},"confidence":0.8}}
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "opencodezen/jev-1.13",
            Input = "evidence",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "is-urgent", Instructions = "Is it urgent?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "boolean-choice", Instructions = "Choose exact value.",
                    Choices = [new() { Value = "true" }, new() { Value = true }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "Rate severity.",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal(0.9, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal("boolean-choice", choice.Name);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(0.4, score.Score);
        Assert.Equal(new[] { 0d, 0.5, 1d }, score.Probabilities.Select(item => item.Value));
        Assert.Equal(24, result.Usage.TotalTokens);
        Assert.True(result.AdditionalProperties!.ContainsKey("providerMetadata"));
        Assert.True(result.AdditionalProperties.ContainsKey("warnings"));
    }

    [Fact]
    public async Task Model_discovery_marks_documented_jev_models_as_decisions()
    {
        var provider = Create((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("/zen/v1/models", http.RequestUri!.AbsolutePath);
            Assert.Null(http.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"data":[{"id":"jev-1.13","owned_by":"TypeSafe AI","type":"language"}]}
                    """, Encoding.UTF8, "application/json")
            });
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal("decision", models["opencodezen/jev-1.13"].Type);
        Assert.Equal("decision", models["opencodezen/jev-1.13-free"].Type);
        Assert.Equal("Jev 1.13", models["opencodezen/jev-1.13"].Name);
    }

    [Fact]
    public async Task Unsupported_options_and_upstream_errors_are_explicit()
    {
        var noSend = Create((_, _) => throw new InvalidOperationException("Request must not be sent."));
        var request = BooleanRequest();
        request.ProviderOptions = new()
        {
            ["opencodezen"] = JsonSerializer.SerializeToElement(new { stream = true })
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => noSend.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.Questions["flag"] = new DecisionBooleanQuestion
        {
            Instructions = "Is this true?",
            Criteria = new() { True = "yes" }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => noSend.DecisionRequestAsync(request));

        request = BooleanRequest();
        var failing = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited")
        }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => failing.DecisionRequestAsync(request));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("rate limited", exception.Message);
    }

    [Fact]
    public async Task OpenAI_images_are_rejected_before_sending()
    {
        var provider = Create((_, _) => throw new InvalidOperationException("Request must not be sent."));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "opencodezen/jev-1.13",
            Input = new OpenAIDecisionInputMessage[]
            {
                new() { Content = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                } }
            },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is this relevant?" }]
        }));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(new()
        {
            Model = "opencodezen/jev-1.13",
            State = JsonSerializer.SerializeToElement(new[]
            {
                new { type = "file", mediaType = "image/png", data = new { type = "data", data = "AQ==" } }
            }),
            StateFormat = "sdk",
            Questions = new() { ["relevant"] = new DecisionBooleanQuestion { Instructions = "Is this relevant?" } }
        }));
    }

    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "opencodezen/jev-1.13",
        State = "evidence",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "test" } }
    };

    private static HttpResponseMessage Response(string answers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"model\":\"jev-1.13\",\"answers\":" + answers
                + ",\"usage\":{\"input_tokens\":24,\"output_tokens\":0}}",
                Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private static OpenCodeZenProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "opencodezen-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
