using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Hanzo;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Decisions;

public sealed class HanzoDecisionProviderTests
{
    [Fact]
    public async Task Native_contract_sends_all_question_types_and_maps_answers()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal("https://api.hanzo.ai/v1/decisions", http.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("hanzo-key", http.Headers.Authorization.Parameter);
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-hanzo-trace")));

            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("typesafe/jev-1.13", body.GetProperty("model").GetString());
            Assert.Equal(JsonSerializer.SerializeToElement(new { ticket = "refund", count = 2 }).GetRawText(),
                body.GetProperty("state").GetRawText());
            Assert.Equal("noul", body.GetProperty("questions").GetProperty("boolean").GetProperty("type").GetString());
            Assert.Equal("choice", body.GetProperty("questions").GetProperty("team").GetProperty("type").GetString());
            Assert.Equal("score", body.GetProperty("questions").GetProperty("urgency").GetProperty("type").GetString());
            Assert.Equal("custom-provider", body.GetProperty("provider").GetProperty("order")[0].GetString());
            Assert.Equal("trace-value", body.GetProperty("trace").GetString());
            Assert.Equal("session", body.GetProperty("session_id").GetString());
            Assert.Equal("customer", body.GetProperty("user").GetString());
            Assert.Equal(7, body.EnumerateObject().Count());
            return Response("""
                {
                  "id":"dec-id","model":"typesafe/jev-1.13","provider":"Hanzo",
                  "answers":{
                    "boolean":{"type":"noul","noul":0.9},
                    "team":{"type":"choice","choice":"payments","confidence":0.88,
                      "probabilities":{"account":0.12,"payments":0.88}},
                    "urgency":{"type":"score","score":1,"confidence":0.5,
                      "legend":{"0":"later","1":"today","2":"now"},
                      "probabilities":{"0":0.25,"1":0.5,"2":0.25}}
                  },
                  "usage":{"input_tokens":42,"output_tokens":0},
                  "routing":{"backend":"openrouter","upstream":"upstream-id"},
                  "state_hash":"sha256:abc","latency_ms":12
                }
                """, "response-id");
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "hanzo/typesafe/jev-1.13",
            State = JsonSerializer.SerializeToElement(new { ticket = "refund", count = 2 }),
            Headers = new()
            {
                ["Authorization"] = "attacker",
                ["x-api-key"] = "attacker",
                ["x-hanzo-trace"] = "trace"
            },
            ProviderOptions = new()
            {
                ["hanzo"] = JsonSerializer.SerializeToElement(new
                {
                    provider = new { order = new[] { "custom-provider" } },
                    trace = "trace-value",
                    session_id = "session",
                    user = "customer"
                })
            },
            Questions = new()
            {
                ["boolean"] = new DecisionBooleanQuestion
                {
                    Instructions = "Is this a refund request?",
                    Criteria = new() { True = "refund request", False = "other request" }
                },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["account"] = "logins", ["payments"] = "charges" }
                },
                ["urgency"] = new DecisionScoreQuestion
                {
                    Instructions = "How urgent?",
                    Criteria = ["later", "today", "now"]
                }
            }
        });

        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(response.Answers["boolean"]).Probability);
        var choice = Assert.IsType<DecisionChoiceAnswer>(response.Answers["team"]);
        Assert.Equal("payments", choice.Choice);
        Assert.Equal(0.88, choice.Probabilities!["payments"]);
        var score = Assert.IsType<DecisionScoreAnswer>(response.Answers["urgency"]);
        Assert.Equal(1, score.Score);
        Assert.Equal(42, response.Usage!.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal("hanzo/typesafe/jev-1.13", response.Response!.ModelId);
        Assert.Equal("dec-id", response.Response.Id);
        Assert.Equal("response-id", response.Response.Headers!.Single(item =>
            item.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.True(response.ProviderMetadata!["hanzo"].GetProperty("routing").TryGetProperty("upstream", out _));
        Assert.True(JsonElement.DeepEquals(response.ProviderMetadata["hanzo"], Assert.IsType<JsonElement>(response.Response.Body)));
    }

    [Fact]
    public async Task Kai_accepts_string_state_and_uses_native_model_id()
    {
        var provider = Create(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("kai", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("evidence", body.RootElement.GetProperty("state").GetString());
            return Response("""
                {"model":"kai","answers":{"flag":{"type":"noul","noul":0.4}},
                 "usage":{"input_tokens":4,"output_tokens":0},"latency_ms":5}
                """);
        });
        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "hanzo/kai",
            State = "evidence",
            Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Is the flag set?" } }
        });

        Assert.Equal(0.4, Assert.IsType<DecisionBooleanAnswer>(response.Answers["flag"]).Probability);
        Assert.Equal("hanzo/kai", response.Response!.ModelId);
    }

    [Fact]
    public async Task Unsupported_options_openai_contract_and_invalid_questions_are_rejected_before_send()
    {
        var provider = Create((_, _) => throw new InvalidOperationException("Request must not be sent."));
        var request = BooleanRequest();
        request.ProviderOptions = new() { ["hanzo"] = JsonSerializer.SerializeToElement(new { unknown = true }) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.ProviderOptions = new() { ["hanzo"] = JsonSerializer.SerializeToElement(new { model = "override" }) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.State = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.Questions["choice"] = new DecisionChoiceQuestion
        {
            Instructions = "Choose.", Criteria = new() { ["only"] = null }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "hanzo/kai", Input = "state",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it true?" }]
        }));
    }

    [Fact]
    public async Task Question_limit_and_option_lengths_are_checked()
    {
        var provider = Create((_, _) => throw new InvalidOperationException("Request must not be sent."));
        var request = BooleanRequest();
        request.Questions = Enumerable.Range(0, 101).ToDictionary(
            index => index.ToString(), _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "test" });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request = BooleanRequest();
        request.ProviderOptions = new()
        {
            ["hanzo"] = JsonSerializer.SerializeToElement(new { session_id = new string('s', 257) })
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Upstream_status_is_preserved_and_invalid_distributions_are_rejected()
    {
        var failed = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => failed.DecisionRequestAsync(BooleanRequest()));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("rate limited", error.Message);

        var malformed = Create((_, _) => Task.FromResult(Response("""
            {"model":"kai","answers":{"flag":{"type":"choice","choice":"a",
              "confidence":0.9,"probabilities":{"a":0.9,"b":0.2}}},
             "usage":{"input_tokens":2,"output_tokens":0}}
            """)));
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionChoiceQuestion
        {
            Instructions = "Choose.", Criteria = new() { ["a"] = null, ["b"] = null }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => malformed.DecisionRequestAsync(request));
    }

    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "hanzo/kai",
        State = "state",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Is it true?" } }
    };

    private static HttpResponseMessage Response(string body, string? requestId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (requestId is not null)
            response.Headers.Add("x-request-id", requestId);
        return response;
    }

    private static HanzoProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "hanzo-key";
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
