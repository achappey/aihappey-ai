using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Celeris;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Celeris;

public sealed class CelerisProviderTests
{
    [Fact]
    public async Task Chat_uses_the_model_scoped_endpoint_and_bearer_authentication()
    {
        var provider = Create(async (request, cancellationToken) =>
        {
            Assert.Equal("https://inference.celeris.ai/celeris-1/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("celeristest", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("celeris-1", body.RootElement.GetProperty("model").GetString());
            return JsonResponse("""
                {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"celeris-1",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
                 "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """);
        });

        var response = await provider.CompleteChatAsync(new()
        {
            Model = "celeris-1",
            Messages = [new() { Role = "user", Content = JsonSerializer.SerializeToElement("hello") }]
        });

        Assert.Equal("chatcmpl-1", response.Id);
    }

    [Fact]
    public async Task Responses_uses_the_model_scoped_native_endpoint()
    {
        var provider = Create(async (request, cancellationToken) =>
        {
            Assert.Equal("https://inference.celeris.ai/celeris-1/v1/responses", request.RequestUri!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("celeris-1", body.RootElement.GetProperty("model").GetString());
            return JsonResponse("""
                {"id":"resp-1","object":"response","created_at":1751673600,"status":"completed",
                 "model":"celeris-1","output":[],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}
                """);
        });

        var response = await provider.ResponsesAsync(new()
        {
            Model = "celeris-1",
            Input = "hello"
        });

        Assert.Equal("resp-1", response.Id);
    }

    [Fact]
    public async Task Decisions_routes_openai_and_system_one_contracts_to_the_documented_model_path()
    {
        var provider = Create(async (request, cancellationToken) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("celeris-1-decision", body.RootElement.GetProperty("model").GetString());
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/decisions", StringComparison.Ordinal))
            {
                Assert.Equal("/celeris-1-decision/v1/decisions", request.RequestUri.AbsolutePath);
                return JsonResponse("""
                    {"model":"celeris-1-decision",
                     "answers":[{"type":"predicate","name":"positive","probability":0.9}],
                     "usage":{"input_tokens":8,"output_tokens":2,"total_tokens":10}}
                    """);
            }

            Assert.Equal("/celeris-1-decision/v1/systemone", request.RequestUri.AbsolutePath);
            var questions = body.RootElement.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("positive").GetProperty("type").GetString());
            Assert.Equal("choice", questions.GetProperty("route").GetProperty("type").GetString());
            Assert.Equal("Billing", questions.GetProperty("route").GetProperty("criteria").GetProperty("billing").GetString());
            Assert.Equal("score", questions.GetProperty("severity").GetProperty("type").GetString());
            Assert.Equal("high", questions.GetProperty("severity").GetProperty("criteria")[1].GetString());
            return JsonResponse("""
                {"model":"celeris-1-decision",
                 "answers":{
                   "positive":{"type":"noul","noul":0.9},
                   "route":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"support":0.2}},
                   "severity":{"type":"score","score":0.75,"probabilities":{"0":0.25,"1":0.75}}
                 },
                 "usage":{"input_tokens":8,"output_tokens":2}}
                """);
        });

        var openAIResponse = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "celeris-1.1-decision",
            Input = "A positive review",
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "positive", Instructions = "Is it positive?" }]
        });
        Assert.Equal("celeris-1.1-decision", openAIResponse.Model);
        Assert.Equal(0.9, Assert.IsType<OpenAIDecisionPredicateAnswer>(openAIResponse.Answers.Single()).Probability);

        var systemOneResponse = await provider.DecisionRequestAsync(new()
        {
            Model = "celeris/celeris-1.1-decision",
            State = "A positive review",
            Questions = new()
            {
                ["positive"] = new DecisionBooleanQuestion { Instructions = "Is it positive?" },
                ["route"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle it?",
                    Criteria = new() { ["billing"] = "Billing", ["support"] = "Support" }
                },
                ["severity"] = new DecisionScoreQuestion
                {
                    Instructions = "How severe is it?",
                    Criteria = ["low", "high"]
                }
            }
        });
        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(systemOneResponse.Answers["positive"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(systemOneResponse.Answers["route"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(systemOneResponse.Answers["severity"]).Score);
        Assert.Equal(8, systemOneResponse.Usage!.InputTokens);
        Assert.Equal("celeris/celeris-1.1-decision", systemOneResponse.Response!.ModelId);
    }

    [Fact]
    public async Task Model_discovery_queries_both_model_scoped_catalogs_and_marks_decision_type()
    {
        var provider = Create((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var models = request.RequestUri!.AbsolutePath switch
            {
                "/celeris-1/v1/models" => """{"data":[{"id":"celeris-1","owned_by":"Celeris"}]}""",
                "/celeris-1-decision/v1/models" => """{"data":[{"id":"celeris-1.1-decision","owned_by":"Celeris"}]}""",
                _ => throw new InvalidOperationException($"Unexpected model endpoint: {request.RequestUri}")
            };
            return Task.FromResult(JsonResponse(models));
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal("celeris/celeris-1", models["celeris/celeris-1"].Id);
        Assert.Equal("decision", models["celeris/celeris-1.1-decision"].Type);
        Assert.Equal("Celeris 1.1 Decision", models["celeris/celeris-1.1-decision"].Name);
    }

    private static CelerisProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "celeristest";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
