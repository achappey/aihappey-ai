using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.LevantoLabs;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Decisions;

public sealed class LevantoLabsProviderDecisionTests
{
    [Fact]
    public async Task SystemOne_maps_supported_questions_reasoning_usage_and_metadata()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            Assert.Equal("/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("sage-latest", root.GetProperty("model").GetString());
            Assert.Equal("auto", root.GetProperty("reasoning").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("state").ValueKind);
            var questions = root.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("approved").GetProperty("type").GetString());
            Assert.Equal("choice", questions.GetProperty("route").GetProperty("type").GetString());
            Assert.Equal(2, questions.GetProperty("route").GetProperty("criteria").EnumerateObject().Count());
            Assert.Equal("score", questions.GetProperty("urgency").GetProperty("type").GetString());
            Assert.Equal(3, questions.GetProperty("urgency").GetProperty("criteria").GetArrayLength());
            return Response("""
                {
                  "model":"levanto-sage-v1.3",
                  "answers":{
                    "approved":{"type":"noul","noul":0.92},
                    "route":{"type":"choice","choice":"option-1","confidence":0.8,
                      "probabilities":{"option-0":0.2,"option-1":0.8}},
                    "urgency":{"type":"score","score":1.25,"confidence":0.5,
                      "probabilities":{"0":0.25,"1":0.25,"2":0.5}}
                  },
                  "usage":{"input_tokens":412,"output_tokens":3}
                }
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "levantolabs/sage-latest",
            Input = "Duplicate charge. The customer says they were charged twice.",
            AdditionalProperties = new()
            {
                ["reasoning"] = JsonSerializer.SerializeToElement("auto")
            },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion
                {
                    Name = "approved",
                    Instructions = "Should the request be approved?"
                },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route",
                    Instructions = "Which team should handle this?",
                    Choices = [new() { Value = "billing" }, new() { Value = "support" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "urgency",
                    Instructions = "Rate urgency.",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal("levanto-sage-v1.3", result.Model);
        Assert.Equal(3, result.Answers.Count);
        Assert.Equal(0.92, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal("support", choice.Choice.Value.GetString());
        Assert.Equal(0.8, choice.Confidence);
        Assert.Equal(0.8, choice.Probabilities[1].Probability);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(1.25, score.Score);
        Assert.Equal(new[] { "low", "medium", "high" }, score.Probabilities.Select(item => item.Label));
        Assert.Equal(412, result.Usage.InputTokens);
        Assert.Equal(415, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task Decision_contract_uses_systemone_and_preserves_provider_response_metadata()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            Assert.Equal("/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-levantolabs-trace")));
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("on", body.RootElement.GetProperty("reasoning").GetString());
            return Response("""
                {
                  "model":"levanto-sage-v1.3",
                  "answers":{"flag":{"type":"noul","noul":0.73}},
                  "usage":{"input_tokens":20,"output_tokens":1}
                }
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "levantolabs/levanto-sage-v1.3",
            State = JsonSerializer.SerializeToElement(new { text = "evidence" }),
            Reasoning = JsonSerializer.SerializeToElement("on"),
            Headers = new()
            {
                ["Authorization"] = "attacker",
                ["x-levantolabs-trace"] = "trace"
            },
            Questions = new()
            {
                ["flag"] = new DecisionBooleanQuestion { Instructions = "Is the claim supported?" }
            }
        });

        Assert.Equal(0.73, Assert.IsType<DecisionBooleanAnswer>(response.Answers["flag"]).Probability);
        Assert.Equal(20, response.Usage!.InputTokens);
        Assert.Equal("levantolabs/levanto-sage-v1.3", response.Response!.ModelId);
        Assert.Equal("request-id", response.Response.Id);
        Assert.True(response.ProviderMetadata!.ContainsKey("levantolabs"));
    }

    [Fact]
    public async Task Native_decide_supports_documented_image_and_grounding_inputs()
    {
        var calls = 0;
        var provider = CreateProvider(async (http, ct) =>
        {
            calls++;
            Assert.Equal("/decide", http.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("yesno", root.GetProperty("question").GetProperty("kind").GetString());
            if (calls == 1)
            {
                var content = root.GetProperty("content");
                Assert.Equal("image", content.GetProperty("kind").GetString());
                Assert.Equal("data:image/png;base64,AQ==", content.GetProperty("media").GetString());
                Assert.Equal("Screenshot context", content.GetProperty("text").GetString());
            }
            else
            {
                Assert.Equal("low_confidence", root.GetProperty("grounding").GetProperty("trigger").GetString());
                Assert.Equal(0.8, root.GetProperty("grounding").GetProperty("confidence_floor").GetDouble());
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"bug","kind":"yesno","result":{"answer":"yes","probability":0.93},
                     "meta":{"model":"levanto-sage-v1.3","usage":{"input_tokens":96,"output_tokens":1}}}
                    """, Encoding.UTF8, "application/json")
            };
        });

        var image = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "sage-latest",
            Input = new OpenAIDecisionInputMessage[]
            {
                new()
                {
                    Content = new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputText { Text = "Screenshot context" },
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                    }
                }
            },
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "bug", Instructions = "Does it show the bug?" }]
        });
        Assert.Equal(0.93, Assert.IsType<OpenAIDecisionPredicateAnswer>(image.Answers.Single()).Probability);

        var grounded = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "sage-latest",
            Input = "Has there been a major incident this month?",
            AdditionalProperties = new()
            {
                ["grounding"] = JsonSerializer.SerializeToElement(new
                {
                    trigger = "low_confidence",
                    confidence_floor = 0.8
                })
            },
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "bug", Instructions = "Is it true?" }]
        });
        Assert.Equal(2, calls);
        Assert.Single(grounded.Answers);
    }

    [Fact]
    public async Task Rejects_unsupported_options_models_and_invalid_upstream_responses()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Request must not be sent."));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "jev-latest",
            Input = "evidence",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it true?" }]
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "sage-latest",
            Input = "evidence",
            AdditionalProperties = new()
            {
                ["reasoning"] = JsonSerializer.SerializeToElement("always")
            },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it true?" }]
        }));

        var failing = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("quota exhausted")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => failing.DecisionRequestAsync(BooleanRequest()));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("quota exhausted", error.Message);

        var nullResponse = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => nullResponse.DecisionRequestAsync(BooleanRequest()));
    }

    [Fact]
    public async Task Model_catalog_lists_documented_aliases_as_decision_models_and_honors_cancellation()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Model listing must not make a request."));
        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal(2, models.Count);
        Assert.All(models.Values, model => Assert.Equal("decision", model.Type));
        Assert.Contains("levantolabs/levanto-sage-v1.3", models.Keys);
        Assert.Contains("levantolabs/sage-latest", models.Keys);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.DecisionRequestAsync(BooleanRequest(), cancellation.Token));
    }

    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "levantolabs/sage-latest",
        State = "evidence",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Is it true?" } }
    };

    private static HttpResponseMessage Response(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private static LevantoLabsProvider CreateProvider(
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
