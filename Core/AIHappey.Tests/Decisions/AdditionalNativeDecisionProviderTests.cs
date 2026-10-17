using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Foundry;
using AIHappey.Core.Providers.LLMGateway;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.Decisions;

public sealed class AdditionalNativeDecisionProviderTests
{
    [Fact]
    public async Task LLMGateway_uses_openai_decisions_for_both_contracts_and_accepts_optional_usage_details()
    {
        var provider = CreateGateway(async (http, ct) =>
        {
            Assert.Equal("/v1/decisions", http.RequestUri!.AbsolutePath);
            Assert.Equal("******", http.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("openai/gpt-6-luna-decisions", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("{\"ticket\":\"refund\"}", body.RootElement.GetProperty("input").GetString());
            Assert.Equal("predicate", body.RootElement.GetProperty("questions")[0].GetProperty("type").GetString());
            return OpenAIResponse("""
                [{"type":"predicate","name":"refund","probability":0.91}]
                """);
        });

        var native = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "openai/gpt-6-luna-decisions",
            Input = "{\"ticket\":\"refund\"}",
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "refund", Instructions = "Is a refund requested?" }]
        });
        Assert.Equal(0.91, Assert.IsType<OpenAIDecisionPredicateAnswer>(native.Answers.Single()).Probability);
        Assert.Equal(7, native.Usage.InputTokens);
        Assert.Equal(0, native.Usage.OutputTokensDetails.ReasoningTokens);

        var universalProvider = CreateGateway((http, ct) =>
        {
            Assert.Equal("/v1/decisions", http.RequestUri!.AbsolutePath);
            return Task.FromResult(OpenAIResponse("""
                [{"type":"choice","name":"route","choice":"billing","probabilities":[
                  {"value":"billing","probability":0.8},{"value":"support","probability":0.2}],"confidence":0.8}]
                """));
        });
        var response = await universalProvider.DecisionRequestAsync(new()
        {
            Model = "openai/gpt-6-luna-decisions",
            State = JsonSerializer.SerializeToElement(new { ticket = "refund" }),
            Questions = new()
            {
                ["route"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle this?",
                    Criteria = new() { ["billing"] = "Payments", ["support"] = "Other questions" }
                }
            }
        });
        var choice = Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]);
        Assert.Equal("billing", choice.Choice);
        Assert.Equal(0.8, choice.Probabilities!["billing"]);
        Assert.Equal(7, response.Usage!.InputTokens);
    }

    [Fact]
    public async Task Foundry_maps_native_questions_answers_probabilities_and_usage()
    {
        var provider = CreateFoundry(async (http, ct) =>
        {
            Assert.Equal("/providers/microsoft/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Null(http.RequestUri.Query);
            Assert.Equal("******", http.Headers.Authorization!.ToString());
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-foundry-trace")));
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("decision-deployment", root.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("state").ValueKind);
            Assert.Equal("noul", root.GetProperty("questions").GetProperty("boolean").GetProperty("type").GetString());
            Assert.Equal("choice", root.GetProperty("questions").GetProperty("choice").GetProperty("type").GetString());
            Assert.Equal(2, root.GetProperty("questions").GetProperty("score").GetProperty("criteria").GetArrayLength());
            return FoundryResponse("""
                {
                  "boolean":{"type":"noul","noul":0.9},
                  "choice":{"type":"choice","choice":"support","probabilities":{"billing":0.2,"support":0.8},"confidence":0.8},
                  "score":{"type":"score","score":0.75,"legend":{"0":"low","1":"high"},
                    "probabilities":{"0":0.25,"1":0.75},"confidence":0.75}
                }
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "foundry/decision-deployment",
            State = JsonSerializer.SerializeToElement(new { ticket = "refund" }),
            Headers = new() { ["Authorization"] = "attacker", ["x-foundry-trace"] = "trace" },
            ProviderOptions = new() { ["foundry"] = JsonSerializer.SerializeToElement(new { }) },
            Questions = new()
            {
                ["boolean"] = new DecisionBooleanQuestion { Instructions = "Is it a refund request?" },
                ["choice"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?", Criteria = new() { ["billing"] = "Payments", ["support"] = null }
                },
                ["score"] = new DecisionScoreQuestion { Instructions = "Rate it.", Criteria = ["low", "high"] }
            }
        });

        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(response.Answers["boolean"]).Probability);
        Assert.Equal("support", Assert.IsType<DecisionChoiceAnswer>(response.Answers["choice"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["score"]).Score);
        Assert.Equal(12, response.Usage!.InputTokens);
        Assert.Equal(4, response.Usage.OutputTokens);
        Assert.Equal("foundry/microsoft-decision-1", response.Response!.ModelId);
        Assert.Equal("request-id", response.Response.Id);
        Assert.True(response.ProviderMetadata!.ContainsKey("foundry"));
    }

    [Fact]
    public async Task Foundry_openai_contract_maps_score_scale_and_rejects_unsupported_images()
    {
        var provider = CreateFoundry(async (http, ct) =>
        {
            Assert.Equal("/providers/microsoft/v1/systemone", http.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("text evidence", body.RootElement.GetProperty("state").GetString());
            Assert.Equal("option-1", body.RootElement.GetProperty("questions").GetProperty("question-0")
                .GetProperty("criteria").EnumerateObject().Last().Name);
            return FoundryResponse("""
                {"question-0":{"type":"score","score":1.25,"legend":{"0":"low","1":"medium","2":"high"},
                  "probabilities":{"0":0.25,"1":0.25,"2":0.5},"confidence":0.5}}
                """);
        });

        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "foundry/decision-deployment",
            Input = "text evidence",
            Questions = [new OpenAIDecisionScoreQuestion
            {
                Instructions = "Rate the severity.",
                Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
            }]
        });

        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers.Single());
        Assert.Equal(0.625, score.Score);
        Assert.Equal(new[] { 0d, 0.5d, 1d }, score.Probabilities.Select(item => item.Value));
        Assert.Equal(16, response.Usage.TotalTokens);

        var noSend = CreateFoundry((_, _) => throw new InvalidOperationException("Request must not be sent."));
        await Assert.ThrowsAsync<NotSupportedException>(() => noSend.OpenAIDecisionRequestAsync(new()
        {
            Model = "foundry/decision-deployment",
            Input = new OpenAIDecisionInputMessage[] { new() { Content = new OpenAIDecisionInputPart[]
                { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" } } } },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is this relevant?" }]
        }));
    }

    [Fact]
    public async Task Foundry_preserves_upstream_errors_and_rejects_invalid_probability_distributions()
    {
        var failing = CreateFoundry((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("quota exhausted") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => failing.DecisionRequestAsync(BooleanRequest()));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("quota exhausted", error.Message);

        var malformed = CreateFoundry((_, _) => Task.FromResult(FoundryResponse("""
            {"flag":{"type":"choice","choice":"a","probabilities":{"a":0.2,"b":0.7},"confidence":0.8}}
            """)));
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionChoiceQuestion
        {
            Instructions = "Pick.", Criteria = new() { ["a"] = null, ["b"] = null }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => malformed.DecisionRequestAsync(request));
    }

    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "foundry/decision-deployment",
        State = "evidence",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "test" } }
    };

    private static HttpResponseMessage OpenAIResponse(string answers)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"model\":\"openai/gpt-6-luna-decisions\",\"answers\":" + answers
                + ",\"usage\":{\"input_tokens\":7,\"output_tokens\":0,\"total_tokens\":7}}",
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage FoundryResponse(string answers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"model\":\"microsoft-decision-1\",\"answers\":" + answers
                + ",\"usage\":{\"input_tokens\":12,\"output_tokens\":4}}",
                Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private static LLMGatewayProvider CreateGateway(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            CreateFactory(responder));

    private static FoundryProvider CreateFoundry(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), Options.Create(new FoundryProviderOptions { Endpoint = "decision-test-resource" }),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), CreateFactory(responder));

    private static IHttpClientFactory CreateFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new ClientFactory(new HttpClient(new Handler(responder)));

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
