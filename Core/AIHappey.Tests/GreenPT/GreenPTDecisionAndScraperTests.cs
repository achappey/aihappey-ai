using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.GreenPT;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.GreenPT;

public sealed class GreenPTDecisionAndScraperTests
{
    [Fact]
    public async Task Decisions_sends_native_systemone_contract_and_preserves_usage_and_impact()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://api.greenpt.ai/v1/systemone", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("greenpt-key", http.Headers.Authorization.Parameter);
            using var request = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = request.RootElement;
            Assert.Equal("metis", body.GetProperty("model").GetString());
            Assert.Equal("support ticket", body.GetProperty("state").GetString());
            Assert.Equal(3, body.GetProperty("questions").EnumerateObject().Count());
            Assert.Equal("noul", body.GetProperty("questions").GetProperty("outage").GetProperty("type").GetString());
            Assert.Equal("Is a production service down?",
                body.GetProperty("questions").GetProperty("outage").GetProperty("instructions").GetString());
            Assert.Equal("Payments",
                body.GetProperty("questions").GetProperty("team").GetProperty("criteria").GetProperty("billing").GetString());
            Assert.Equal(JsonValueKind.Array, body.GetProperty("questions").GetProperty("severity").GetProperty("criteria").ValueKind);
            return Json("""
                {"model":"metis","answers":{
                  "outage":{"type":"noul","noul":0.95},
                  "team":{"type":"choice","choice":"technical","probabilities":{"billing":0.1,"technical":0.9},"confidence":0.8},
                  "severity":{"type":"score","score":1.7,"legend":{"0":"low","1":"medium","2":"high"},
                    "probabilities":{"0":0.1,"1":0.1,"2":0.8},"confidence":0.8}},
                 "usage":{"input_tokens":24,"output_tokens":0},
                 "impact":{"inferenceTime":{"total":72,"unit":"ms"}}}
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "greenpt/metis",
            State = "support ticket",
            Questions = new()
            {
                ["outage"] = new DecisionBooleanQuestion { Instructions = "Is a production service down?" },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["billing"] = "Payments", ["technical"] = "Errors and outages" }
                },
                ["severity"] = new DecisionScoreQuestion
                {
                    Instructions = "How severe?",
                    Criteria = ["low", "medium", "high"]
                }
            }
        });

        Assert.Equal(0.95, Assert.IsType<DecisionBooleanAnswer>(result.Answers["outage"]).Probability);
        Assert.Equal("technical", Assert.IsType<DecisionChoiceAnswer>(result.Answers["team"]).Choice);
        Assert.Equal(1.7, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score);
        Assert.Equal(24, result.Usage!.InputTokens);
        Assert.Equal(0, result.Usage.OutputTokens);
        Assert.Equal("greenpt/metis", result.Response!.ModelId);
        Assert.Equal(72, result.ProviderMetadata!["greenpt"].GetProperty("impact")
            .GetProperty("inferenceTime").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task OpenAI_decisions_maps_answers_and_normalized_score_probabilities()
    {
        var provider = Create(async (http, ct) =>
        {
            using var request = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = request.RootElement;
            Assert.Equal("metis", body.GetProperty("model").GetString());
            Assert.Equal("case description", body.GetProperty("state").GetString());
            return Json("""
                {"model":"metis","answers":{
                  "question-0":{"type":"noul","noul":0.75},
                  "question-1":{"type":"choice","choice":"option-1","probabilities":{"option-0":0.2,"option-1":0.8},"confidence":0.6},
                  "question-2":{"type":"score","score":1.5,"legend":{"0":"low","1":"mid","2":"high"},
                    "probabilities":{"0":0.25,"1":0,"2":0.75},"confidence":0.75}},
                 "usage":{"input_tokens":30,"output_tokens":0},"impact":{}}
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "greenpt/metis",
            Input = "case description",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "is-urgent", Instructions = "Is this urgent?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route",
                    Instructions = "Which team?",
                    Choices = [new() { Value = "billing" }, new() { Value = "technical" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "severity",
                    Instructions = "How severe?",
                    Levels = [new() { Label = "low" }, new() { Label = "mid" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal(0.75, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        Assert.Equal("technical", Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]).Choice.Value.GetString());
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(0.75, score.Score);
        Assert.Equal(new[] { 0d, 0.5d, 1d }, score.Probabilities.Select(item => item.Value));
        Assert.Equal(30, result.Usage.TotalTokens);
        Assert.True(result.AdditionalProperties!.ContainsKey("providerMetadata"));
    }

    [Fact]
    public async Task Scraper_routes_Vercel_file_url_to_native_markdown_endpoint()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal("https://api.greenpt.ai/v1/tools/crawl/scrape", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            using var payload = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("https://example.com/article", payload.RootElement.GetProperty("url").GetString());
            Assert.Equal("markdown", payload.RootElement.GetProperty("formats")[0].GetString());
            return Json("""{"success":true,"data":{"markdown":"# Article\n\nClean text"}}""");
        });
        var request = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"greenpt/scraper","messages":[{"role":"user","content":[
              {"type":"file","file":{"file_data":"https://example.com/article","filename":"article.html"}}
            ]}]}
            """, JsonSerializerOptions.Web)!;

        var result = await provider.CompleteChatAsync(request);

        Assert.Contains("# Article", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Scraper_routes_OpenAI_responses_file_url_and_returns_text_output()
    {
        var provider = Create(async (http, ct) =>
        {
            using var payload = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("https://example.com/response-input", payload.RootElement.GetProperty("url").GetString());
            Assert.Equal(new[] { "markdown" }, payload.RootElement.GetProperty("formats").EnumerateArray()
                .Select(format => format.GetString()));
            return Json("""{"success":true,"data":{"markdown":"# Response input"}}""");
        });
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"greenpt/scraper","input":[{"type":"message","role":"user","content":[
              {"type":"input_file","file_url":"https://example.com/response-input"}
            ]}]}
            """, ResponseJson.Default)!;

        var result = await provider.ResponsesAsync(request);

        Assert.Contains("# Response input", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Model_discovery_marks_metis_as_decision_and_adds_scraper()
    {
        var provider = Create((http, _) => Task.FromResult(Json("""{"data":[{"id":"metis"},{"id":"scraper"}]}""")));
        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal("decision", models["greenpt/metis"].Type);
        Assert.Equal("language", models["greenpt/scraper"].Type);
    }

    [Fact]
    public async Task Native_endpoints_preserve_upstream_http_status_and_error_body()
    {
        var decisionProvider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited")
        }));
        var decisionError = await Assert.ThrowsAsync<HttpRequestException>(() =>
            decisionProvider.DecisionRequestAsync(new()
            {
                Model = "greenpt/metis",
                State = "case",
                Questions = new() { ["q"] = new DecisionBooleanQuestion { Instructions = "Is it true?" } }
            }));
        Assert.Equal(HttpStatusCode.TooManyRequests, decisionError.StatusCode);
        Assert.Contains("rate limited", decisionError.Message);

        var scraperProvider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid URL")
        }));
        var scraperError = await Assert.ThrowsAsync<HttpRequestException>(() =>
            scraperProvider.ExecuteUnifiedAsync(new AIRequest
            {
                ProviderId = "greenpt",
                Model = "greenpt/scraper",
                Input = new AIInput
                {
                    Items =
                    [
                        new AIInputItem
                        {
                            Type = "message",
                            Role = "user",
                            Content = [new AIFileContentPart { Type = "file", Data = "https://example.com" }]
                        }
                    ]
                }
            }));
        Assert.Equal(HttpStatusCode.BadRequest, scraperError.StatusCode);
        Assert.Contains("invalid URL", scraperError.Message);
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GreenPTProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "greenpt-key";
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
