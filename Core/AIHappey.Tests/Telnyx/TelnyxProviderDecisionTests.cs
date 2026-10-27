using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Telnyx;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Telnyx;

public sealed class TelnyxProviderDecisionTests
{
    [Fact]
    public async Task Decision_contract_sends_system_one_questions_and_preserves_native_metadata()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("/v2/ai/typesafe/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("telnyx/decision-flash", root.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("state").ValueKind);
            var questions = root.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("active_incident").GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Object, questions.GetProperty("active_incident").GetProperty("instructions").ValueKind);
            Assert.Equal("Yes", questions.GetProperty("active_incident").GetProperty("criteria").GetProperty("true").GetString());
            Assert.Equal("choice", questions.GetProperty("team").GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, questions.GetProperty("team").GetProperty("criteria").GetProperty("sales").ValueKind);
            Assert.Equal(4, questions.GetProperty("urgency").GetProperty("criteria").GetArrayLength());
            Assert.Equal(JsonValueKind.Array, questions.GetProperty("urgency").GetProperty("instructions").ValueKind);
            return DecisionResponse("""
                {
                  "model":"telnyx/decision-flash",
                  "answers":{
                    "team":{"type":"choice","choice":"support","confidence":0.92,
                      "probabilities":{"billing":0.08,"support":0.92,"sales":0.0}},
                    "active_incident":{"type":"noul","noul":0.99},
                    "urgency":{"type":"score","score":2.75,"legend":{"0":"Low","1":"Normal","2":"High","3":"Critical"},
                      "probabilities":{"0":0.0,"1":0.0,"2":0.25,"3":0.75},"confidence":0.9}
                  },
                  "usage":{"input_tokens":125,"output_tokens":4}
                }
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "telnyx/decision-flash",
            State = JsonSerializer.SerializeToElement(new { ticket = "T-1001", content = "Calls are failing." }),
            Questions = new()
            {
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle this?",
                    Criteria = new() { ["billing"] = "Payments", ["support"] = "Technical issues", ["sales"] = null }
                },
                ["active_incident"] = new DecisionBooleanQuestion
                {
                    Instructions = JsonSerializer.SerializeToElement(new { question = "Is this an active incident?" }),
                    Criteria = new() { True = "Yes", False = "No" }
                },
                ["urgency"] = new DecisionScoreQuestion
                {
                    Instructions = JsonSerializer.SerializeToElement(new[] { "Rate urgency.", "Use the supplied context." }),
                    Criteria = ["Low", "Normal", "High", "Critical"]
                }
            }
        });

        Assert.Equal("support", Assert.IsType<DecisionChoiceAnswer>(response.Answers["team"]).Choice);
        Assert.Equal(0.92, Assert.IsType<DecisionChoiceAnswer>(response.Answers["team"]).Probabilities!["support"]);
        Assert.Equal(0.99, Assert.IsType<DecisionBooleanAnswer>(response.Answers["active_incident"]).Probability);
        Assert.Equal(2.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["urgency"]).Score);
        Assert.Equal(125, response.Usage!.InputTokens);
        Assert.Equal(4, response.Usage.OutputTokens);
        Assert.Equal("telnyx/decision-flash", response.Response!.ModelId);
        Assert.Equal("request-id", response.Response.Id);
        Assert.True(response.ProviderMetadata!["telnyx"].TryGetProperty("answers", out _));
    }

    [Fact]
    public async Task OpenAI_contract_maps_choice_values_and_uses_decision_pro()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("telnyx/decision-pro", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("text evidence", body.RootElement.GetProperty("state").GetString());
            var question = body.RootElement.GetProperty("questions").GetProperty("question-0");
            Assert.Equal("choice", question.GetProperty("type").GetString());
            Assert.Contains("true: Escalate", question.GetProperty("criteria").EnumerateObject()
                .Select(option => option.Value.GetString()));
            return DecisionResponse("""
                {"model":"telnyx/decision-pro",
                 "answers":{"question-0":{"type":"choice","choice":"option-1","confidence":0.8,
                   "probabilities":{"option-0":0.2,"option-1":0.8}}},
                 "usage":{"input_tokens":8,"output_tokens":1}}
                """);
        });

        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "telnyx/decision-pro",
            Input = "text evidence",
            Questions =
            [
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route",
                    Instructions = "Where should this go?",
                    Choices = [new() { Value = "billing" }, new() { Value = true, Description = "Escalate" }]
                }
            ]
        });

        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(Assert.Single(response.Answers));
        Assert.Equal("route", choice.Name);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(0.8, choice.Confidence);
        Assert.Equal(0.8, choice.Probabilities[1].Probability);
        Assert.Equal("telnyx/decision-pro", response.Model);
        Assert.Equal(9, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task Decision_models_are_cataloged_as_decisions()
    {
        var provider = CreateProvider((http, _) => Task.FromResult(http.RequestUri!.AbsolutePath switch
        {
            "/v2/ai/models" => JsonResponse("""
                {"data":[
                  {"id":"telnyx/decision-flash","owned_by":"Telnyx"},
                  {"id":"telnyx/decision-pro","owned_by":"Telnyx"}
                ]}
                """),
            "/v2/ai/openai/embeddings/models" => JsonResponse("""{"data":[]}"""),
            "/v2/text-to-speech/voices" => JsonResponse("""{"voices":[]}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {http.RequestUri}")
        }));

        var models = await provider.ListModels();
        foreach (var id in new[] { "telnyx/decision-flash", "telnyx/decision-pro" })
        {
            Assert.Equal("decision", Assert.Single(models, model => model.Id == id).Type);
            Assert.DoesNotContain(models, model => model.Id == $"telnyx/{id}");
        }
    }

    [Fact]
    public async Task Unsupported_models_options_and_question_limits_are_rejected_before_sending()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Request must not be sent."));
        var request = BooleanRequest();
        request.Model = "telnyx/unknown";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.ProviderOptions = new()
        {
            ["telnyx"] = JsonSerializer.SerializeToElement(new { stream = false })
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.Questions["route"] = new DecisionChoiceQuestion
        {
            Instructions = "Choose one.", Criteria = new() { ["only"] = "one" }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.Questions = Enumerable.Range(0, 65).ToDictionary(
            index => index.ToString(),
            _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "Is it valid?" });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Decision_contract_rejects_image_input_and_preserves_upstream_errors()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Request must not be sent."));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "telnyx/decision-pro",
            Input = new OpenAIDecisionInputMessage[]
            {
                new()
                {
                    Content = new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                    }
                }
            },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is the image relevant?" }]
        }));

        provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"error\":{\"message\":\"Invalid request\"}}", Encoding.UTF8, "application/json")
        }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.DecisionRequestAsync(BooleanRequest()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains("Invalid request", exception.Message);
    }

    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "telnyx/decision-flash",
        State = "evidence",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Is it valid?" } }
    };

    private static HttpResponseMessage DecisionResponse(string json)
    {
        var response = JsonResponse(json);
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static TelnyxProvider CreateProvider(
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
