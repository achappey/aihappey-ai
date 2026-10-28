using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Baseten;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Baseten;

public sealed class BasetenProviderDecisionTests
{
    private const string Elephant = "decision-models/baseten-elephant-9b-latest";

    [Fact]
    public async Task Typed_request_maps_all_system_one_question_types_and_preserves_metadata()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/systemone", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            Assert.Equal("trace", Assert.Single(request.Headers.GetValues("x-baseten-trace")));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal(Elephant, body.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Object, body.GetProperty("state").ValueKind);
            Assert.Equal("priority", body.GetProperty("service_tier").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("refund").GetProperty("type").GetString());
            Assert.Equal("choice", questions.GetProperty("route").GetProperty("type").GetString());
            Assert.Equal("score", questions.GetProperty("urgency").GetProperty("type").GetString());
            Assert.Equal("Billing, payments, and refunds",
                questions.GetProperty("route").GetProperty("criteria").GetProperty("billing").GetString());
            Assert.Equal("Immediate emergency",
                questions.GetProperty("urgency").GetProperty("criteria")[2].GetString());
            return Response("""
                {"model":"decision-models/baseten-elephant-9b-latest","answers":{
                  "refund":{"type":"noul","noul":0.988},
                  "route":{"type":"choice","choice":"billing","confidence":0.9985,
                    "probabilities":{"billing":0.9992,"technical_support":0.0008}},
                  "urgency":{"type":"score","score":0.5143,"confidence":0.2286,
                    "legend":{"0":"Routine request","1":"Needs attention today","2":"Immediate emergency"},
                    "probabilities":{"0":0.5124,"1":0.4610,"2":0.0267}}},
                 "usage":{"input_tokens":429,"output_tokens":3}}
                """);
        });
        var request = new DecisionRequest
        {
            Model = "baseten/" + Elephant,
            State = JsonSerializer.SerializeToElement(new { ticket = "refund" }),
            Questions = new()
            {
                ["refund"] = new DecisionBooleanQuestion { Instructions = "Is the customer asking for a refund?" },
                ["route"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle this request?",
                    Criteria = new()
                    {
                        ["billing"] = "Billing, payments, and refunds",
                        ["technical_support"] = "Technical troubleshooting"
                    }
                },
                ["urgency"] = new DecisionScoreQuestion
                {
                    Instructions = "How urgent is the request?",
                    Criteria = ["Routine request", "Needs attention today", "Immediate emergency"]
                }
            },
            Headers = new() { ["x-baseten-trace"] = "trace" },
            ProviderOptions = new()
            {
                ["baseten"] = JsonSerializer.SerializeToElement(new { service_tier = "priority" })
            }
        };

        var result = await provider.DecisionRequestAsync(request);
        Assert.Equal(0.988, Assert.IsType<DecisionBooleanAnswer>(result.Answers["refund"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(0.5143, Assert.IsType<DecisionScoreAnswer>(result.Answers["urgency"]).Score);
        Assert.Equal(429, result.Usage!.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal("baseten/" + Elephant, result.Response!.ModelId);
        Assert.Equal("baseten-request-id", result.Response.Id);
        Assert.True(result.ProviderMetadata!["baseten"].GetProperty("answers").TryGetProperty("route", out _));
    }

    [Fact]
    public async Task SDK_state_maps_text_and_remote_or_inline_images_to_system_one_messages()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            var content = body.GetProperty("state").GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("Evaluate this screenshot.", content[0].GetProperty("text").GetString());
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            Assert.Equal("https://cdn.example.test/screenshot.png",
                content[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("data:image/png;base64,AQID",
                content[2].GetProperty("image_url").GetProperty("url").GetString());
            return BooleanResponse();
        });
        var request = new DecisionRequest
        {
            Model = "baseten/" + Elephant,
            StateFormat = "sdk",
            State = JsonSerializer.SerializeToElement(new object[]
            {
                new { type = "text", text = "Evaluate this screenshot." },
                new { type = "file", mediaType = "image/png", data = new { type = "url", url = "https://cdn.example.test/screenshot.png" } },
                new { type = "file", mediaType = "image/png", data = new { type = "data", data = "AQID" } }
            }),
            Questions = new()
            {
                ["visible"] = new DecisionBooleanQuestion { Instructions = "Is an error visible?" }
            }
        };

        var result = await provider.DecisionRequestAsync(request);
        Assert.Equal(0.75, Assert.IsType<DecisionBooleanAnswer>(result.Answers["visible"]).Probability);
    }

    [Fact]
    public async Task OpenAI_contract_preserves_question_order_images_choice_values_and_scores()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("default", body.GetProperty("service_tier").GetString());
            var content = body.GetProperty("state").GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,AQID",
                content[1].GetProperty("image_url").GetProperty("url").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("question-0").GetProperty("type").GetString());
            Assert.Equal("noul", questions.GetProperty("question-1").GetProperty("type").GetString());
            Assert.Equal("score", questions.GetProperty("question-2").GetProperty("type").GetString());
            Assert.Equal("escalation", questions.GetProperty("question-1").GetProperty("criteria").GetProperty("true").GetString());
            return Response("""
                {"model":"decision-models/baseten-elephant-9b-latest","answers":{
                  "question-0":{"type":"noul","noul":0.7},
                  "question-1":{"type":"noul","noul":0.8},
                  "question-2":{"type":"score","score":1.25,"confidence":0.6,
                    "legend":{"0":"low: minor","1":"medium","2":"high: severe"},
                    "probabilities":{"0":0,"1":0.75,"2":0.25}}},
                 "usage":{"input_tokens":16,"output_tokens":3}}
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "baseten/" + Elephant,
            Input = new OpenAIDecisionInput(new[]
            {
                new OpenAIDecisionInputMessage
                {
                    Content = new OpenAIDecisionMessageContent(new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputText { Text = "Check this screenshot." },
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQID" }
                    })
                }
            }),
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "flag", Instructions = "Is an error visible?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route",
                    Instructions = "Should this escalate?",
                    Choices =
                    [
                        new() { Value = true, Description = "escalation" },
                        new() { Value = false }
                    ]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "severity",
                    Instructions = "How severe?",
                    Levels =
                    [
                        new() { Label = "low", Description = "minor" },
                        new() { Label = "medium" },
                        new() { Label = "high", Description = "severe" }
                    ]
                }
            ],
            AdditionalProperties = new()
            {
                ["service_tier"] = JsonSerializer.SerializeToElement("default")
            }
        });

        Assert.Equal(new string?[] { "flag", "route", "severity" }, result.Answers.Select(answer => answer.Name));
        Assert.Equal(0.7, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(0.8, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(1.25, score.Score);
        Assert.Equal(new[] { "low", "medium", "high" }, score.Probabilities.Select(item => item.Label));
        Assert.Equal(16, result.Usage.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal(19, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task Discovery_includes_serverless_models_with_decision_catalog_type()
    {
        var provider = CreateProvider((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse($$"""
                {"data":[{"id":"{{Elephant}}","name":"Elephant-1","context_length":16384}]}
                """));
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);
        var elephant = models["baseten/" + Elephant];
        var mammoth = models["baseten/decision-models/baseten-mammoth-27b-latest"];
        Assert.Single(models.Values, model => model.Id == "baseten/" + Elephant);
        Assert.Equal("decision", elephant.Type);
        Assert.Equal("decision", mammoth.Type);
        Assert.Equal(16_384, elephant.ContextWindow);
        Assert.Equal(16_384, mammoth.ContextWindow);
        Assert.Equal("Elephant-1", elephant.Name);
        Assert.Equal("Mammoth-1", mammoth.Name);
    }

    [Fact]
    public async Task Unsupported_models_options_and_upstream_errors_are_explicit()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("quota exhausted", Encoding.UTF8, "application/json")
        }));
        var request = new DecisionRequest
        {
            Model = "custom-deployment",
            State = "evidence",
            Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Is it true?" } }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));

        request.Model = "baseten/" + Elephant;
        request.MaxOutputTokens = 12;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.MaxOutputTokens = null;
        request.ProviderOptions = new()
        {
            ["baseten"] = JsonSerializer.SerializeToElement(new { unsupported = true })
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.ProviderOptions = new()
        {
            ["baseten"] = JsonSerializer.SerializeToElement(new { service_tier = "flex" })
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request.ProviderOptions = null;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(request));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("quota exhausted", error.Message);

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "baseten/" + Elephant,
            Input = new OpenAIDecisionInput("evidence"),
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it true?" }],
            SafetyIdentifier = "user-id"
        }));
    }

    private static BasetenProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage BooleanResponse()
        => Response("""
            {"model":"decision-models/baseten-elephant-9b-latest",
             "answers":{"visible":{"type":"noul","noul":0.75}},
             "usage":{"input_tokens":8,"output_tokens":1}}
            """);

    private static HttpResponseMessage Response(string json)
    {
        var response = JsonResponse(json);
        response.Headers.Add("x-request-id", "baseten-request-id");
        return response;
    }

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
