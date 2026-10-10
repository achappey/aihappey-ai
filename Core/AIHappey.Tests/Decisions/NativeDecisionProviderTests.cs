using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Core.Providers.Perplexity;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Decisions;

public sealed class NativeDecisionProviderTests
{
    [Theory]
    [InlineData("deepinfra")]
    [InlineData("perplexity")]
    public async Task Sdk_contract_preserves_structured_evidence_and_maps_answers(string providerId)
    {
        var request = new DecisionRequest
        {
            Model = providerId + "/" + ModelName(providerId),
            State = JsonSerializer.SerializeToElement(new { evidence = new[] { 1, 2 }, type = "arbitrary" }),
            Questions = new()
            {
                ["boolean"] = new DecisionBooleanQuestion
                {
                    Instructions = JsonSerializer.SerializeToElement(new { rule = "allowed" }),
                    Criteria = new() { True = "yes", False = null }
                },
                ["choice"] = new DecisionChoiceQuestion
                {
                    Instructions = "pick", Criteria = new() { ["a"] = null, ["b"] = "second" }
                },
                ["score"] = new DecisionScoreQuestion { Instructions = "rate", Criteria = ["low", "high"] }
            },
            Headers = new() { ["Authorization"] = "Bearer attacker", ["x-api-key"] = "attacker",
                ["x-" + providerId + "-key"] = "attacker", ["x-" + providerId + "-trace"] = "trace" },
            ProviderOptions = new() { [providerId] = JsonSerializer.SerializeToElement(new { }) }
        };
        var provider = CreateProvider(providerId, async (http, ct) =>
        {
            Assert.Equal("/v1/decisions", http.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("Bearer test-key", http.Headers.Authorization!.ToString());
            Assert.False(http.Headers.Contains("x-api-key"));
            Assert.False(http.Headers.Contains("x-" + providerId + "-key"));
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-" + providerId + "-trace")));
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal(3, root.EnumerateObject().Count());
            Assert.Equal(ModelName(providerId), root.GetProperty("model").GetString());
            Assert.Equal(request.State.Value.GetRawText(), root.GetProperty("state").GetRawText());
            var boolean = root.GetProperty("questions").GetProperty("boolean");
            Assert.Equal("noul", boolean.GetProperty("type").GetString());
            Assert.Equal("allowed", boolean.GetProperty("instructions").GetProperty("rule").GetString());
            Assert.Equal(JsonValueKind.Null, boolean.GetProperty("criteria").GetProperty("false").ValueKind);
            return JsonResponse(ModelName(providerId), """
                {"score":{"type":"score","score":0.75,"legend":{"0":"low","1":"high"},"probabilities":{"0":0.25,"1":0.75},"confidence":0.75},
                 "choice":{"type":"choice","choice":"b","probabilities":{"a":0.2,"b":0.8},"confidence":0.8},
                 "boolean":{"type":"noul","noul":0.9}}
                """);
        });
        var response = await provider.DecisionRequestAsync(request);
        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(response.Answers["boolean"]).Probability);
        Assert.Equal("b", Assert.IsType<DecisionChoiceAnswer>(response.Answers["choice"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["score"]).Score);
        Assert.Equal(10, response.Usage!.InputTokens);
        Assert.Equal(3, response.Usage.OutputTokens);
        Assert.Equal(request.Model, response.Response!.ModelId);
        Assert.Equal("request-id", response.Response.Id);
        Assert.True(response.ProviderMetadata!.ContainsKey(providerId));
    }

    [Theory]
    [InlineData("deepinfra")]
    [InlineData("perplexity")]
    public async Task OpenAI_contract_preserves_order_names_typed_choices_and_score_labels(string providerId)
    {
        var provider = CreateProvider(providerId, async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var questions = body.RootElement.GetProperty("questions");
            Assert.Equal(new[] { "question-0", "question-1", "question-2" }, questions.EnumerateObject().Select(p => p.Name));
            Assert.Equal(new[] { "option-0", "option-1" }, questions.GetProperty("question-1").GetProperty("criteria").EnumerateObject().Select(p => p.Name));
            return JsonResponse(ModelName(providerId), """
                {"question-2":{"type":"score","score":1.25,"legend":{"0":"low","1":"medium","2":"high"},"probabilities":{"0":0.25,"1":0.25,"2":0.5},"confidence":0.5},
                 "question-1":{"type":"choice","choice":"option-1","probabilities":{"option-0":0.2,"option-1":0.8},"confidence":0.8},
                 "question-0":{"type":"noul","noul":0.7}}
                """);
        });
        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = providerId + "/" + ModelName(providerId), Input = "state",
            Questions = [
                new OpenAIDecisionPredicateQuestion { Instructions = "allowed?", Name = "repeated" },
                new OpenAIDecisionChoiceQuestion { Instructions = "pick", Name = "repeated",
                    Choices = [new() { Value = "true" }, new() { Value = true }] },
                new OpenAIDecisionScoreQuestion { Instructions = "rate",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }] }
            ]
        });
        Assert.Equal(new string?[] { "repeated", "repeated", null }, response.Answers.Select(answer => answer.Name));
        Assert.Equal(0.7, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal(0.8, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]);
        Assert.Equal(0.625, score.Score);
        Assert.Equal(new[] { "low", "medium", "high" }, score.Probabilities.Select(p => p.Label));
        Assert.Equal(new[] { 0d, 0.5, 1d }, score.Probabilities.Select(p => p.Value));
        Assert.Equal(13, response.Usage.TotalTokens);
        Assert.Equal(ModelName(providerId), response.Model);
        Assert.Equal(0, response.Usage.InputTokensDetails.CachedTokens);
    }

    [Theory]
    [InlineData("{\"flag\":{\"noul\":0.4}}")]
    [InlineData("{\"flag\":{\"type\":\"noul\",\"noul\":0.4}}")]
    public async Task DeepInfra_accepts_documented_default_answer_type(string answers)
    {
        var provider = CreateProvider("deepinfra", (_, _) => Task.FromResult(JsonResponse("org/decision", answers)));
        var result = await provider.DecisionRequestAsync(BooleanRequest());
        Assert.Equal(0.4, Assert.IsType<DecisionBooleanAnswer>(result.Answers["flag"]).Probability);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"flag\":{\"noul\":1.1}}")]
    [InlineData("{\"flag\":{\"type\":\"choice\",\"noul\":0.4}}")]
    [InlineData("{\"flag\":{\"noul\":0.4},\"extra\":{\"noul\":0.4}}")]
    [InlineData("{\"flag\":{\"noul\":0.4},\"flag\":{\"noul\":0.5}}")]
    [InlineData("{\"flag\":{\"noul\":\"not a number\"}}")]
    public async Task DeepInfra_rejects_malformed_answers(string answers)
    {
        var provider = CreateProvider("deepinfra", (_, _) => Task.FromResult(JsonResponse("org/decision", answers)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(BooleanRequest()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task DeepInfra_rejects_question_counts_before_sending(int count)
    {
        var provider = NoSendProvider();
        var request = BooleanRequest();
        request.Questions = Enumerable.Range(0, count).ToDictionary(i => i.ToString(), _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "test" });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(53)]
    public async Task DeepInfra_rejects_choice_limits(int count)
    {
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionChoiceQuestion { Instructions = "pick",
            Criteria = Enumerable.Range(0, count).ToDictionary(i => i.ToString(), _ => (DecisionInput?)null) };
        await Assert.ThrowsAsync<ArgumentException>(() => NoSendProvider().DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(11, false)]
    [InlineData(2, true)]
    public async Task DeepInfra_rejects_score_limits_and_null_criteria(int count, bool hasNull)
    {
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionScoreQuestion { Instructions = "rate",
            Criteria = Enumerable.Range(0, count).Select(_ => hasNull ? null : (DecisionInput?)new DecisionInput("level")).ToArray() };
        await Assert.ThrowsAsync<ArgumentException>(() => NoSendProvider().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task DeepInfra_rejects_unsupported_options_images_and_honors_cancellation()
    {
        var provider = NoSendProvider();
        var request = BooleanRequest();
        request.ProviderOptions = new() { ["deepinfra"] = JsonSerializer.SerializeToElement(new { model = "override" }) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        var openAI = new OpenAIDecisionRequest { Model = "org/decision", Input = "text",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "test" }], SafetyIdentifier = "unsupported" };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(openAI));
        openAI.SafetyIdentifier = null;
        openAI.Input = new OpenAIDecisionInputMessage[] { new() { Content = new OpenAIDecisionInputPart[]
            { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" } } } };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(openAI));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DecisionRequestAsync(BooleanRequest(), cancellation.Token));
    }

    [Theory]
    [InlineData("deepinfra")]
    [InlineData("perplexity")]
    public async Task Native_provider_preserves_upstream_failure_status_and_body(string providerId)
    {
        var provider = CreateProvider(providerId, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("quota exhausted") }));
        var request = BooleanRequest();
        request.Model = ModelName(providerId);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(request));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("quota exhausted", exception.Message);
    }

    [Fact]
    public async Task Perplexity_keeps_image_mapping_and_detail_warning()
    {
        // A 1x1 PNG header is sufficient for the existing dimension validation.
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1 };
        var url = "data:image/png;base64," + Convert.ToBase64String(bytes);
        var provider = CreateProvider("perplexity", async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var state = body.RootElement.GetProperty("state");
            Assert.Equal("image_url", state[0].GetProperty("type").GetString());
            Assert.Equal(url, state[0].GetProperty("image_url").GetProperty("url").GetString());
            return JsonResponse(ModelName("perplexity"), """{"question-0":{"type":"noul","noul":0.5}}""");
        });
        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = ModelName("perplexity"),
            Input = new OpenAIDecisionInputMessage[] { new() { Content = new OpenAIDecisionInputPart[]
                { new OpenAIDecisionInputImage { ImageUrl = url, Detail = "high" } } } },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "test" }]
        });
        Assert.Equal(2, response.AdditionalProperties!["warnings"].GetArrayLength());
    }

    [Fact]
    public async Task Perplexity_still_rejects_remote_images()
    {
        var provider = CreateProvider("perplexity", (_, _) => throw new InvalidOperationException("Must not send"));
        var request = BooleanRequest();
        request.Model = ModelName("perplexity");
        request.State = JsonSerializer.SerializeToElement(new { type = "image_url", image_url = new { url = "https://example.com/image.png" } });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    private static string ModelName(string provider) => provider == "deepinfra" ? "org/decision" : "pplx-decider-v1.1-27b";
    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "deepinfra/org/decision", State = "evidence",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "test" } }
    };
    private static IModelProvider NoSendProvider()
        => CreateProvider("deepinfra", (_, _) => throw new InvalidOperationException("Must not send"));

    private static IModelProvider CreateProvider(string provider, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var factory = new ClientFactory(new HttpClient(new Handler(responder)));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return provider == "deepinfra"
            ? new DeepInfraProvider(new KeyResolver(), factory, cache)
            : new PerplexityProvider(new KeyResolver(), cache, factory);
    }

    private static HttpResponseMessage JsonResponse(string model, string answers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"model\":" + JsonSerializer.Serialize(model) + ",\"answers\":" + answers
                + ",\"usage\":{\"input_tokens\":10,\"output_tokens\":3}}", Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
