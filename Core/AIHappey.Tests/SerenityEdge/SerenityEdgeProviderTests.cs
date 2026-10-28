using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.SerenityEdge;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SerenityEdge;

public sealed class SerenityEdgeProviderTests
{
    [Fact]
    public async Task ModelsAreCataloguedByAdvertisedEndpoint()
    {
        var provider = CreateProvider((request, _) => Task.FromResult(JsonResponse("""
            {"data":[
              {"id":"orion-compass","name":"Orion Compass","owned_by":"Serenity Edge","endpoints":["/v1/decisions"]},
              {"id":"qwen3-embedding-0.6b","name":"Qwen Embedding","owned_by":"Serenity Edge","endpoints":["/v1/embeddings"]},
              {"id":"orion-plus","name":"Orion Plus","owned_by":"Serenity Edge","endpoints":["/v1/chat/completions","/v1/responses"]},
              {"id":"qwen3-reranker-0.6b","name":"Qwen Reranker","owned_by":"Serenity Edge","endpoints":["/v1/rerank"]}
            ]}
            """)));

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal("decision", models["serenityedge/orion-compass"].Type);
        Assert.Equal("embedding", models["serenityedge/qwen3-embedding-0.6b"].Type);
        Assert.Equal("language", models["serenityedge/orion-plus"].Type);
        Assert.Equal("reranking", models["serenityedge/qwen3-reranker-0.6b"].Type);
    }

    [Fact]
    public async Task DecisionsSubmitPredicateChoiceAndScoreQuestionsNatively()
    {
        HttpRequestMessage? sent = null;
        string? requestBodyText = null;
        var provider = CreateProvider((request, _) =>
        {
            sent = request;
            return CaptureAndRespond(request);

            async Task<HttpResponseMessage> CaptureAndRespond(HttpRequestMessage httpRequest)
            {
                requestBodyText = await httpRequest.Content!.ReadAsStringAsync();
                return JsonResponse("""
                {
                  "model":"orion-compass-26.1",
                  "answers":[
                    {"type":"predicate","name":"is-positive","probability":0.8},
                    {"type":"choice","name":"category","choice":"positive","confidence":0.9,
                     "probabilities":[{"value":"positive","probability":0.9},{"value":"negative","probability":0.1}]},
                    {"type":"score","name":"rating","score":0.4,"confidence":0.6,
                     "probabilities":[{"value":0,"label":"low","probability":0.6},{"value":1,"label":"high","probability":0.4}]}
                  ],
                  "usage":{"input_tokens":42,"input_tokens_details":{},"output_tokens":0,"output_tokens_details":{},"total_tokens":42}
                }
                """);
            }
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "orion-compass",
            Input = "A positive review",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "is-positive", Instructions = "Is this positive?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "category",
                    Instructions = "Classify sentiment.",
                    Choices = [new() { Value = "positive" }, new() { Value = "negative" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "rating",
                    Instructions = "Rate sentiment.",
                    Levels = [new() { Label = "low" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("https://api.serenityedge.ai/v1/decisions", sent.RequestUri!.ToString());
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        using var requestBody = JsonDocument.Parse(requestBodyText!);
        Assert.Equal(new[] { "predicate", "choice", "score" },
            requestBody.RootElement.GetProperty("questions").EnumerateArray()
                .Select(question => question.GetProperty("type").GetString()).ToArray());
        Assert.Equal("orion-compass-26.1", result.Model);
        Assert.Equal(3, result.Answers.Count);
        Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]);
        Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
    }

    [Fact]
    public async Task VercelScoreDecisionsMapNativeOrderedLevels()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""
            {
              "model":"orion-compass-26.1",
              "answers":[
                {"type":"score","name":"rating","score":0.4,"confidence":0.6,
                 "probabilities":[{"value":0,"label":"low","probability":0.6},{"value":1,"label":"high","probability":0.4}]}
              ],
              "usage":{"input_tokens":42,"input_tokens_details":{},"output_tokens":0,"output_tokens_details":{},"total_tokens":42}
            }
            """)));

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "orion-compass",
            State = "A positive review",
            Questions = new()
            {
                ["rating"] = new DecisionScoreQuestion
                {
                    Instructions = "Rate sentiment.",
                    Criteria = [null, null]
                }
            }
        });

        var answer = Assert.IsType<DecisionScoreAnswer>(result.Answers["rating"]);
        Assert.Equal(0.4, answer.Score, 6);
        Assert.Equal(new[] { "0", "1" }, answer.Probabilities!.Keys.OrderBy(key => key).ToArray());
    }

    [Fact]
    public async Task EmbeddingsUseNativeEndpointAndRejectDimensions()
    {
        HttpRequestMessage? sent = null;
        var provider = CreateProvider((request, _) =>
        {
            sent = request;
            var vector = JsonSerializer.Serialize(new float[1024]);
            return Task.FromResult(JsonResponse(
                $$$"""{"object":"list","data":[{"object":"embedding","index":0,"embedding":{{{vector}}}}],"model":"qwen3-embedding-0.6b","usage":{"prompt_tokens":3,"total_tokens":3}}"""));
        });

        var result = await provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = "qwen3-embedding-0.6b",
            Input = JsonSerializer.SerializeToElement("hello"),
            EncodingFormat = "float"
        });

        Assert.Equal("https://api.serenityedge.ai/v1/embeddings", sent!.RequestUri!.ToString());
        Assert.Equal(1024, result.Data.Single().Embedding.GetArrayLength());
        Assert.Equal("serenityedge/qwen3-embedding-0.6b", result.Model);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = "qwen3-embedding-0.6b",
            Input = JsonSerializer.SerializeToElement("hello"),
            Dimensions = 256
        }));
    }

    [Fact]
    public async Task RerankingUsesNativeEndpointAndMapsRankedIndexes()
    {
        HttpRequestMessage? sent = null;
        var provider = CreateProvider((request, _) =>
        {
            sent = request;
            return Task.FromResult(JsonResponse("""
                {"id":"rerank_1","results":[{"index":1,"relevance_score":0.93}]}
                """));
        });

        var response = await provider.RerankingRequest(new()
        {
            Model = "qwen3-reranker-0.6b",
            Query = "what is Serenity Edge?",
            Documents = new()
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[] { "unrelated", "provider documentation" })
            },
            TopN = 1
        });

        Assert.Equal("https://api.serenityedge.ai/v1/rerank", sent!.RequestUri!.ToString());
        Assert.Single(response.Ranking);
        Assert.Equal(1, response.Ranking.Single().Index);
        Assert.Equal(0.93f, response.Ranking.Single().RelevanceScore);
    }

    [Fact]
    public async Task ChatCompletionsUseNativeRouteAndEnforceDocumentedTokenCap()
    {
        HttpRequestMessage? sent = null;
        var provider = CreateProvider((request, _) =>
        {
            sent = request;
            return Task.FromResult(JsonResponse("""
                {"id":"chatcmpl_1","object":"chat.completion","created":1,"model":"orion-plus",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
                """));
        });

        await provider.CompleteChatAsync(new()
        {
            Model = "orion-plus",
            Messages = [new() { Role = "user", Content = JsonSerializer.SerializeToElement("hello") }]
        });

        Assert.Equal("https://api.serenityedge.ai/v1/chat/completions", sent!.RequestUri!.ToString());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CompleteChatAsync(new()
        {
            Model = "orion-plus",
            AdditionalProperties = new()
            {
                ["max_tokens"] = JsonSerializer.SerializeToElement(32769)
            }
        }));
    }

    [Fact]
    public async Task ResponsesUseNativeStatelessEndpoint()
    {
        HttpRequestMessage? sent = null;
        var provider = CreateProvider((request, _) =>
        {
            sent = request;
            return Task.FromResult(JsonResponse("""
                {"id":"resp_123","object":"response","created_at":1760000000,"status":"completed",
                 "model":"orion-plus","output":[],"store":false,"usage":null}
                """));
        });

        var result = await provider.ResponsesAsync(new()
        {
            Model = "orion-plus",
            Input = "hello",
            Store = false
        });

        Assert.Equal("https://api.serenityedge.ai/v1/responses", sent!.RequestUri!.ToString());
        Assert.Equal("serenityedge/orion-plus", result.Model);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new()
        {
            Model = "orion-plus",
            Input = "hello",
            Store = true
        }));
    }

    private static SerenityEdgeProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new TestApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(new HttpClient(new TestHandler(responder))));

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class TestApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "serenityedge" ? "test-key" : null;
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
