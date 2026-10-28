using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.DecisionModels;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.DecisionModels;

public sealed class DecisionModelsProviderTests
{
    [Fact]
    public async Task ListModels_maps_live_registry_to_decision_catalog_types()
    {
        var provider = CreateProvider((request, _) =>
        {
            Assert.Equal("https://api.decisionmodels.io/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse("""
                {"object":"list","data":[
                  {"id":"s1-fast","name":"S1 Fast","owned_by":"Decision Models","max_input_tokens":8000},
                  {"id":"s1-pro-3","name":"S1 Pro 3","max_input_tokens":8192},
                  {"id":"reax-1-20261021","description":"Pinned release"}
                ]}
                """));
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal(3, models.Count);
        Assert.Equal("decision", models["decisionmodels/s1-fast"].Type);
        Assert.Equal("S1 Fast", models["decisionmodels/s1-fast"].Name);
        Assert.Equal(8000, models["decisionmodels/s1-fast"].ContextWindow);
        Assert.Equal("Decision Models", models["decisionmodels/reax-1-20261021"].OwnedBy);
        Assert.All(models.Values, model => Assert.Equal("decision", model.Type));
    }

    [Fact]
    public async Task OpenAI_decisions_map_all_typed_questions_and_usage_to_native_systemone()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/systemone", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("s1-pro-3", document.RootElement.GetProperty("model").GetString());
            var questions = document.RootElement.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("question-0").GetProperty("type").GetString());
            Assert.Equal("choice", questions.GetProperty("question-1").GetProperty("type").GetString());
            Assert.Equal("score", questions.GetProperty("question-2").GetProperty("type").GetString());
            Assert.Equal(new[] { "red", "blue" },
                questions.GetProperty("question-1").GetProperty("criteria").EnumerateObject()
                    .Select(property => property.Name));
            return JsonResponse("""
                {"id":"dec_1","model":"s1-pro-3","answers":{
                  "question-0":{"type":"noul","noul":0.91},
                  "question-1":{"type":"choice","choice":"blue","confidence":0.8,
                    "probabilities":{"red":0.2,"blue":0.8}},
                  "question-2":{"type":"score","score":1.2,"confidence":0.7,
                    "probabilities":{"0":0.2,"1":0.3,"2":0.5}}},
                 "usage":{"input_tokens":20,"output_tokens":0,"decisions":3},"tier":"global"}
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "decisionmodels/s1-pro-3",
            Input = "A bicycle is red.",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Instructions = "Is the bicycle red?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Instructions = "What color is it?",
                    Choices = [new() { Value = "red" }, new() { Value = "blue" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "How certain?",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal(3, result.Answers.Count);
        Assert.Equal(0.91, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal("blue", choice.Choice.Value.GetString());
        Assert.Equal(0.8, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(1.2, score.Score);
        Assert.Equal(new[] { "0", "1", "2" }, score.Probabilities.Select(item => item.Label));
        Assert.Equal(20, result.Usage.InputTokens);
        Assert.Equal(0, result.Usage.OutputTokens);
        Assert.Equal(20, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task Image_decisions_use_multimodal_route_and_eu_region_header()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/multimodal", request.RequestUri!.AbsolutePath);
            Assert.Equal("eu", Assert.Single(request.Headers.GetValues("S1-Region")));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            Assert.Equal(1, body.GetProperty("images").GetArrayLength());
            Assert.Equal("data:image/png;base64,AQ==", body.GetProperty("images")[0].GetString());
            Assert.Equal("Customer return request.", body.GetProperty("state").GetString());
            return JsonResponse("""
                {"model":"s1-vision","answers":{
                  "question-0":{"type":"choice","choice":"damaged","confidence":0.9,
                    "probabilities":{"damaged":0.9,"intact":0.1}}},
                 "usage":{"input_tokens":330,"output_tokens":0,"decisions":1}}
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "s1-vision",
            Input = new OpenAIDecisionInputMessage[]
            {
                new()
                {
                    Content = new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputText { Text = "Customer return request." },
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                    }
                }
            },
            Questions =
            [
                new OpenAIDecisionChoiceQuestion
                {
                    Instructions = "Does the image show damage?",
                    Choices = [new() { Value = "damaged" }, new() { Value = "intact" }]
                }
            ],
            AdditionalProperties = new()
            {
                ["providerOptions"] = JsonSerializer.SerializeToElement(new
                {
                    decisionmodels = new { region = "eu" }
                })
            }
        });

        Assert.Equal("damaged", Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[0]).Choice.Value.GetString());
    }

    [Fact]
    public async Task Native_decisions_support_provider_region_and_reject_model_limits()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/systemone", request.RequestUri!.AbsolutePath);
            Assert.Equal("eu", Assert.Single(request.Headers.GetValues("S1-Region")));
            Assert.Equal("decision-retry-1", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var boolean = document.RootElement.GetProperty("questions").GetProperty("valid");
            Assert.Equal("noul", boolean.GetProperty("type").GetString());
            Assert.Equal("affirmative", boolean.GetProperty("criteria").GetProperty("true").GetString());
            return JsonResponse("""
                {"model":"s1-pro-3","answers":{
                  "valid":{"type":"noul","noul":0.88},
                  "team":{"type":"choice","choice":"support","confidence":0.9,
                    "probabilities":{"billing":0.1,"support":0.9}},
                  "rating":{"type":"score","score":0.5,"confidence":0.75,
                    "probabilities":{"0":0.25,"1":0.75}}},
                 "usage":{"input_tokens":12,"output_tokens":0}}
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "decisionmodels/s1-pro-3",
            State = "Ticket state",
            Headers = new()
            {
                ["S1-Region"] = "eu",
                ["Idempotency-Key"] = "decision-retry-1"
            },
            ProviderOptions = new()
            {
                ["decisionmodels"] = JsonSerializer.SerializeToElement(new { region = "eu" })
            },
            Questions = new()
            {
                ["valid"] = new DecisionBooleanQuestion
                {
                    Instructions = "Is it valid?",
                    Criteria = new() { True = "affirmative", False = "negative" }
                },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["billing"] = "Payment question", ["support"] = "Other question" }
                },
                ["rating"] = new DecisionScoreQuestion
                {
                    Instructions = "Rate it.",
                    Criteria = ["low", "high"]
                }
            }
        });

        Assert.Equal("support", Assert.IsType<DecisionChoiceAnswer>(response.Answers["team"]).Choice);
        Assert.Equal(0.88, Assert.IsType<DecisionBooleanAnswer>(response.Answers["valid"]).Probability);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["rating"]).Score);
        Assert.Equal(12, response.Usage!.InputTokens);
        Assert.Equal("decisionmodels/s1-pro-3", response.Response!.ModelId);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "s1-fast",
            Input = "state",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Instructions = "First?" },
                new OpenAIDecisionPredicateQuestion { Instructions = "Second?" }
            ]
        }));

        var oversized = CreateProvider((_, _) => throw new InvalidOperationException("Request should not be sent."));
        var payloadException = await Assert.ThrowsAsync<HttpRequestException>(() =>
            oversized.OpenAIDecisionRequestAsync(new()
            {
                Model = "s1-fast",
                Input = new string('x', 64_001),
                Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Check it." }]
            }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, payloadException.StatusCode);
    }

    [Fact]
    public async Task Upstream_credit_errors_keep_the_http_status_and_body()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("{\"error\":{\"code\":\"insufficient_credit\"}}")
        }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.OpenAIDecisionRequestAsync(new()
            {
                Model = "s1-fast",
                Input = "state",
                Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it valid?" }]
            }));

        Assert.Equal(HttpStatusCode.PaymentRequired, exception.StatusCode);
        Assert.Contains("insufficient_credit", exception.Message);
    }

    [Fact]
    public void Registration_and_api_key_header_are_wired()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-DecisionModels-Key"] = " test-key ";
        var resolver = new HeaderApiKeyResolver(
            new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("DECISIONMODELS"));
        Assert.Equal("X-DecisionModels-Key", HeaderApiKeyResolver.SupportedProviderHeaders["decisionmodels"]);

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(DecisionModelsProvider));
    }

    private static DecisionModelsProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "decisionmodels" ? "test-key" : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
