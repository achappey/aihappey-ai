using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AnyRouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AnyRouter;

public sealed class AnyRouterProviderCapabilityTests
{
    [Fact]
    public async Task DecisionRequestAsync_posts_native_typed_questions_and_maps_answers()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("/api/v1/decisions", http.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("typesafe/jev", root.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("state").ValueKind);
            Assert.Equal("noul", root.GetProperty("questions").GetProperty("urgent").GetProperty("type").GetString());
            Assert.Equal("choice", root.GetProperty("questions").GetProperty("team").GetProperty("type").GetString());
            Assert.Equal("billing", root.GetProperty("questions").GetProperty("team").GetProperty("criteria").EnumerateObject().First().Name);
            Assert.Equal(3, root.GetProperty("questions").GetProperty("severity").GetProperty("criteria").GetArrayLength());

            return JsonResponse("""
                {
                  "model":"typesafe/jev",
                  "answers":{
                    "urgent":{"type":"noul","noul":0.92},
                    "team":{"type":"choice","choice":"billing","confidence":0.94,
                      "probabilities":{"billing":0.94,"technical":0.06}},
                    "severity":{"type":"score","score":1.5,"confidence":0.8,
                      "legend":{"0":"low","1":"medium","2":"high"},
                      "probabilities":{"0":0.1,"1":0.3,"2":0.6}}
                  },
                  "usage":{"input_tokens":312,"output_tokens":48,"cost":0}
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "anyrouter/typesafe/jev",
            State = JsonSerializer.SerializeToElement(new { message = "payouts failed" }),
            Questions = new()
            {
                ["urgent"] = new DecisionBooleanQuestion { Instructions = "Is this urgent?" },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["billing"] = "Payment issues", ["technical"] = "Product issue" }
                },
                ["severity"] = new DecisionScoreQuestion
                {
                    Instructions = "How severe?",
                    Criteria = ["low", "medium", "high"]
                }
            }
        });

        Assert.Equal(0.92, Assert.IsType<DecisionBooleanAnswer>(result.Answers["urgent"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["team"]).Choice);
        Assert.Equal(0.94, Assert.IsType<DecisionChoiceAnswer>(result.Answers["team"]).Probabilities!["billing"]);
        Assert.Equal(1.5, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score);
        Assert.Equal(312, result.Usage!.InputTokens);
        Assert.Equal(48, result.Usage.OutputTokens);
        Assert.Equal("anyrouter/typesafe/jev", result.Response!.ModelId);
        Assert.Equal("typesafe/jev", result.ProviderMetadata!["anyrouter"].GetProperty("model").GetString());
    }

    [Fact]
    public async Task OpenAIDecisionRequestAsync_preserves_typed_choice_values_and_answers()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("anyrouter/decision", root.GetProperty("model").GetString());
            Assert.Equal("evidence", root.GetProperty("state").GetString());
            Assert.Equal("choice", root.GetProperty("questions").GetProperty("question-0").GetProperty("type").GetString());
            Assert.Equal(2, root.GetProperty("questions").GetProperty("question-0").GetProperty("criteria").EnumerateObject().Count());
            return JsonResponse("""
                {
                  "model":"anyrouter/decision",
                  "answers":{
                    "question-0":{"type":"choice","choice":"option-1","confidence":0.8,
                      "probabilities":{"option-0":0.2,"option-1":0.8}}
                  },
                  "usage":{"input_tokens":6,"output_tokens":2}
                }
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new OpenAIDecisionRequest
        {
            Model = "anyrouter/anyrouter/decision",
            Input = new OpenAIDecisionInput("evidence"),
            Questions =
            [
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route",
                    Instructions = "Where should this go?",
                    Choices =
                    [
                        new() { Value = "billing", Description = "Payments" },
                        new() { Value = true, Description = "Escalate" }
                    ]
                }
            ]
        });

        var answer = Assert.IsType<OpenAIDecisionChoiceAnswer>(Assert.Single(result.Answers));
        Assert.Equal(JsonValueKind.True, answer.Choice.Value.ValueKind);
        Assert.Equal(0.8, answer.Confidence);
        Assert.Equal("route", answer.Name);
        Assert.Equal(6, result.Usage.InputTokens);
        Assert.Equal(2, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task RerankingRequest_posts_documented_options_and_maps_rankings_and_usage()
    {
        var provider = CreateProvider(async (http, ct) =>
        {
            Assert.Equal("/api/v1/rerank", http.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("cohere/rerank-v3.5", root.GetProperty("model").GetString());
            Assert.Equal(2, root.GetProperty("documents").GetArrayLength());
            Assert.Equal("European AI infrastructure.", root.GetProperty("documents")[1].GetProperty("text").GetString());
            Assert.Equal(1, root.GetProperty("top_n").GetInt32());
            Assert.True(root.GetProperty("return_documents").GetBoolean());
            Assert.Equal(128, root.GetProperty("max_tokens_per_doc").GetInt32());
            Assert.Equal("search-1", root.GetProperty("session_id").GetString());
            return JsonResponse("""
                {
                  "id":"rerank-1",
                  "model":"cohere/rerank-v3.5",
                  "results":[
                    {"index":0,"relevance_score":0.31},
                    {"index":1,"relevance_score":0.93,"document":{"text":"European AI infrastructure."}}
                  ],
                  "usage":{"prompt_tokens":22,"total_tokens":22,"search_units":1,"cost":0.002}
                }
                """);
        });

        var result = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "anyrouter/cohere/rerank-v3.5",
            Query = "European sovereign AI",
            TopN = 1,
            Documents = new RerankingDocument
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new object[]
                {
                    "A general document.",
                    new { text = "European AI infrastructure." }
                }, JsonSerializerOptions.Web)
            },
            ProviderOptions = new()
            {
                ["anyrouter"] = JsonSerializer.SerializeToElement(new
                {
                    return_documents = true,
                    max_tokens_per_doc = 128,
                    session_id = "search-1"
                })
            }
        });

        var ranking = Assert.Single(result.Ranking);
        Assert.Equal(1, ranking.Index);
        Assert.Equal(0.93f, ranking.RelevanceScore);
        Assert.Equal("rerank-1", result.Response.Id);
        Assert.Equal("anyrouter/cohere/rerank-v3.5", result.Response.ModelId);
        Assert.Equal(1, result.ProviderMetadata!["anyrouter"].GetProperty("usage").GetProperty("search_units").GetInt32());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ListModels_marks_documented_decision_and_rerank_models_with_correct_types()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""
            {
              "data":[
                {"id":"anyrouter/decision","name":"AnyRouter decision"},
                {"id":"typesafe/jev","name":"Jev"},
                {"id":"cohere/rerank-v3.5","name":"Cohere Rerank v3.5"},
                {"id":"openai/gpt-4.1","name":"GPT-4.1"}
              ]
            }
            """)));

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal("decision", models["anyrouter/anyrouter/decision"].Type);
        Assert.Equal("decision", models["anyrouter/typesafe/jev"].Type);
        Assert.Equal("rerank", models["anyrouter/cohere/rerank-v3.5"].Type);
        Assert.Contains(models.Values, model => model.Id == "anyrouter/typesafe/jev-latest" && model.Type == "decision");
    }

    [Fact]
    public async Task RerankingRequest_rejects_undocumented_options_and_preserves_upstream_errors()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid request", Encoding.UTF8, MediaTypeNames.Application.Json)
        }));
        var invalidOptions = CreateRerankRequest();
        invalidOptions.ProviderOptions = new()
        {
            ["anyrouter"] = JsonSerializer.SerializeToElement(new { unknown_option = true })
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.RerankingRequest(invalidOptions));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.RerankingRequest(CreateRerankRequest()));
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("invalid request", exception.Message);
    }

    private static RerankingRequest CreateRerankRequest()
        => new()
        {
            Model = "anyrouter/cohere/rerank-v3.5",
            Query = "query",
            Documents = TextDocuments("document")
        };

    private static RerankingDocument TextDocuments(params string[] values)
        => new()
        {
            Type = "text",
            Values = JsonSerializer.SerializeToElement(values, JsonSerializerOptions.Web)
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static AnyRouterProvider CreateProvider(
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

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
