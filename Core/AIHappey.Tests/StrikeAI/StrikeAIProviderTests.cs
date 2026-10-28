using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.StrikeAI;
using AIHappey.HeaderAuth;
using AIHappey.HeaderAuth.Controllers;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.StrikeAI;

public sealed class StrikeAIProviderTests
{
    [Fact]
    public async Task Maps_all_native_question_types_and_preserves_metadata_usage_and_headers()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal("https://strike-ai.eu/v1/systemone", http.RequestUri!.ToString());
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("strike-test-key", http.Headers.Authorization.Parameter);
            Assert.Equal("deduplicate", Assert.Single(http.Headers.GetValues("Idempotency-Key")));
            Assert.False(http.Headers.Contains("X-StrikeAI-Key"));
            Assert.False(http.Headers.Contains("X-Other-Trace"));
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("strike-1", body.GetProperty("model").GetString());
            Assert.Equal("refund", body.GetProperty("state").GetProperty("ticket").GetString());
            Assert.Equal("noul", body.GetProperty("questions").GetProperty("flag").GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("questions").GetProperty("route").GetProperty("criteria").GetProperty("support").ValueKind);
            Assert.Equal("high", body.GetProperty("questions").GetProperty("severity").GetProperty("criteria")[1].GetString());
            Assert.True(body.GetProperty("options").GetProperty("return_raw_probabilities").GetBoolean());
            var response = Success("""
                {"flag":{"type":"noul","noul":0.1},
                 "route":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"support":0.2},"confidence":0.8},
                 "severity":{"type":"score","score":0.75,"probabilities":{"0":0.25,"1":0.75},"confidence":0.75}}
                """);
            response.Headers.TryAddWithoutValidation("x-request-id", "trace-id");
            return response;
        });
        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "strikeai/strike-1",
            State = JsonSerializer.SerializeToElement(new { ticket = "refund" }),
            Headers = new()
            {
                ["Idempotency-Key"] = "deduplicate", ["Authorization"] = "attacker",
                ["X-StrikeAI-Key"] = "attacker", ["X-Other-Trace"] = "blocked"
            },
            ProviderOptions = new() { ["strikeai"] = JsonSerializer.SerializeToElement(new { return_raw_probabilities = true }) },
            Questions = new()
            {
                ["flag"] = new DecisionBooleanQuestion { Instructions = "Is it urgent?" },
                ["route"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?", Criteria = new() { ["billing"] = "Payments", ["support"] = null }
                },
                ["severity"] = new DecisionScoreQuestion { Instructions = "How severe?", Criteria = ["low", "high"] }
            }
        });
        Assert.Equal(0.1, Assert.IsType<DecisionBooleanAnswer>(response.Answers["flag"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["severity"]).Score);
        Assert.Equal(12, response.Usage!.InputTokens);
        Assert.Equal(4, response.Usage.OutputTokens);
        Assert.Equal("req_1", response.Response!.Id);
        Assert.Equal("strikeai/strike-1", response.Response.ModelId);
        Assert.Equal("trace-id", response.Response.Headers!["x-request-id"]);
        Assert.Equal("0.000123", response.ProviderMetadata!["strikeai"].GetProperty("usage").GetProperty("cost_eur").GetString());
        Assert.Equal(42, response.ProviderMetadata["strikeai"].GetProperty("meta").GetProperty("latency_ms").GetInt32());
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("{\"ticket\":\"refund\"}")]
    [InlineData("[\"first\",{\"second\":true}]")]
    public async Task Preserves_string_object_and_array_state(string json)
    {
        var provider = Create(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), body.RootElement.GetProperty("state")));
            return Success("""{"flag":{"type":"noul","noul":0.9}}""");
        });
        var request = BooleanRequest();
        request.State = JsonSerializer.Deserialize<JsonElement>(json);
        await provider.DecisionRequestAsync(request);
    }

    [Fact]
    public async Task OpenAI_adapter_preserves_question_order_names_value_types_and_score_scale()
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var questions = document.RootElement.GetProperty("questions");
            Assert.Equal("\"true\": literal", questions.GetProperty("question-1").GetProperty("criteria").GetProperty("option-0").GetString());
            Assert.Equal("true", questions.GetProperty("question-1").GetProperty("criteria").GetProperty("option-1").GetString());
            Assert.Equal("high: High severity", questions.GetProperty("question-2").GetProperty("criteria")[2].GetString());
            Assert.True(document.RootElement.GetProperty("options").GetProperty("return_raw_probabilities").GetBoolean());
            return Success("""
                {"question-0":{"type":"noul","noul":0.2},
                 "question-1":{"type":"choice","choice":"option-1","probabilities":{"option-0":0.1,"option-1":0.9},"confidence":0.9},
                 "question-2":{"type":"score","score":1.25,"probabilities":{"0":0.25,"1":0.25,"2":0.5},"confidence":0.5}}
                """);
        });
        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "strikeai/strike-1", Input = "refund",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "flag", Instructions = "Is it urgent?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route", Instructions = "Pick the exact value.",
                    Choices = [new() { Value = "true", Description = "literal" }, new() { Value = true }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "How severe?",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high", Description = "High severity" }]
                }
            ]
        });
        Assert.Equal(new string?[] { "flag", "route", null }, result.Answers.Select(answer => answer.Name));
        Assert.Equal(0.2, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal(0.9, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(1.25, score.Score);
        Assert.Equal(new[] { 0d, 1d, 2d }, score.Probabilities.Select(item => item.Value));
        Assert.Equal("strike-1", result.Model);
        Assert.Equal(16, result.Usage.TotalTokens);
        Assert.True(result.AdditionalProperties!.ContainsKey("providerMetadata"));
    }

    [Fact]
    public async Task Discovers_every_advertised_model_with_native_ids_context_and_decision_type()
    {
        var calls = 0;
        var provider = Create((http, _) =>
        {
            calls++;
            Assert.Equal("https://strike-ai.eu/v1/models", http.RequestUri!.ToString());
            Assert.Equal("strike-test-key", http.Headers.Authorization!.Parameter);
            return Task.FromResult(Json("""
                {"object":"list","data":[{"id":"strike-1","owned_by":"strike","max_context_tokens":8192},
                  {"id":"advertised-release","name":"Advertised release","owned_by":"strike","max_context_tokens":16384}]}
                """));
        });
        var models = (await provider.ListModels()).ToDictionary(model => model.Id);
        Assert.Equal(2, models.Count);
        Assert.Equal("strike-1", models["strikeai/strike-1"].Name);
        Assert.Equal(8192, models["strikeai/strike-1"].ContextWindow);
        Assert.Equal(16384, models["strikeai/advertised-release"].ContextWindow);
        Assert.All(models.Values, model => Assert.Equal("decision", model.Type));
        await provider.ListModels();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[{"id":""}]}""")]
    [InlineData("""{"data":[{"id":"strike-1","max_context_tokens":0}]}""")]
    [InlineData("""{"data":[{"id":"strike-1"},{"id":"strike-1"}]}""")]
    public async Task Rejects_invalid_model_catalogs(string json)
        => await Assert.ThrowsAsync<JsonException>(() => Create((_, _) => Task.FromResult(Json(json))).ListModels());

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(413)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task Preserves_upstream_error_status_and_body_without_retrying(int status)
    {
        var calls = 0;
        var provider = Create((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"code":"upstream_code","message":"provider detail","request_id":"req_error"}}""")
            });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(BooleanRequest()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("upstream_code", error.Message);
        Assert.Contains("req_error", error.Message);
        Assert.Equal(1, calls);
        var discoveryError = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal((HttpStatusCode)status, discoveryError.StatusCode);
    }

    [Theory]
    [InlineData("""{"flag":{"type":"noul","noul":1.1}}""")]
    [InlineData("""{"flag":{"type":"noul","noul":-0.1}}""")]
    [InlineData("""{"flag":{"type":"noul","noul":"0.2"}}""")]
    [InlineData("""{"flag":{"type":"choice","noul":0.2}}""")]
    [InlineData("""{}""")]
    [InlineData("""{"wrong":{"type":"noul","noul":0.2}}""")]
    [InlineData("""{"flag":{"type":"noul","noul":0.2},"extra":{"type":"noul","noul":0.2}}""")]
    [InlineData("""{"flag":{"type":"noul","noul":0.2},"flag":{"type":"noul","noul":0.3}}""")]
    public async Task Rejects_invalid_or_incomplete_answers(string answers)
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create((_, _) => Task.FromResult(Success(answers))).DecisionRequestAsync(BooleanRequest()));

    [Theory]
    [InlineData("""{"choice":"a","probabilities":{"a":0.2,"b":0.8},"confidence":0.8}""")]
    [InlineData("""{"choice":"a","probabilities":{"a":0.8,"b":0.1},"confidence":0.8}""")]
    [InlineData("""{"choice":"a","probabilities":{"a":0.8,"c":0.2},"confidence":0.8}""")]
    [InlineData("""{"choice":"c","probabilities":{"a":0.8,"b":0.2},"confidence":0.8}""")]
    [InlineData("""{"choice":"a","probabilities":{"a":1.2,"b":-0.2},"confidence":0.8}""")]
    [InlineData("""{"choice":"a","probabilities":{"a":0.8,"b":0.2},"confidence":2}""")]
    public async Task Rejects_invalid_choice_distributions(string fields)
    {
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionChoiceQuestion { Instructions = "Pick.", Criteria = new() { ["a"] = null, ["b"] = null } };
        var answer = """{"flag":{"type":"choice",""" + fields[1..] + "}";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create((_, _) => Task.FromResult(Success(answer))).DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Rejects_invalid_weighted_scores(double score)
    {
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionScoreQuestion { Instructions = "Rate.", Criteria = ["low", "high"] };
        var answer = JsonSerializer.Serialize(new
        {
            flag = new { type = "score", score, confidence = 0.8, probabilities = new Dictionary<string, double> { ["0"] = 0.2, ["1"] = 0.8 } }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create((_, _) => Task.FromResult(Success(answer))).DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Allows_optional_distributions_for_sdk_but_requires_them_for_OpenAI()
    {
        var provider = Create((_, _) => Task.FromResult(Success("""{"flag":{"type":"choice","choice":"a","confidence":0.8}}""")));
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionChoiceQuestion { Instructions = "Pick.", Criteria = new() { ["a"] = null, ["b"] = null } };
        Assert.Null(Assert.IsType<DecisionChoiceAnswer>((await provider.DecisionRequestAsync(request)).Answers["flag"]).Probabilities);
        provider = Create((_, _) => Task.FromResult(Success("""{"question-0":{"type":"choice","choice":"option-0","confidence":0.8}}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "strike-1", Input = "text",
            Questions = [new OpenAIDecisionChoiceQuestion { Instructions = "Pick.", Choices = [new() { Value = "a" }, new() { Value = "b" }] }]
        }));
    }

    [Fact]
    public async Task Rejects_unsupported_options_and_inputs_before_sending()
    {
        var provider = NoSend();
        var request = BooleanRequest();
        request.MaxOutputTokens = 10;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.MaxOutputTokens = null;
        request.Reasoning = JsonSerializer.SerializeToElement(new { effort = "low" });
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.Reasoning = null;
        request.ProviderOptions = new() { ["strikeai"] = JsonSerializer.SerializeToElement(new { temperature = 0 }) };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.ProviderOptions["strikeai"] = JsonSerializer.SerializeToElement(new { return_raw_probabilities = "yes" });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request.ProviderOptions = null;
        request.Questions["flag"] = new DecisionBooleanQuestion
        {
            Instructions = "Flag?", Criteria = new() { True = "yes" }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.Questions["flag"] = new DecisionBoxQuestion { Instructions = "Locate." };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.Questions["flag"] = new DecisionBooleanQuestion { Instructions = JsonSerializer.SerializeToElement(new { structured = true }) };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.Questions = BooleanRequest().Questions;
        request.StateFormat = "sdk";
        request.State = JsonSerializer.Deserialize<JsonElement>("""[{"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQ=="}}]""");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "strike-1",
            Input = new OpenAIDecisionInputMessage[] { new() { Content = new OpenAIDecisionInputPart[]
                { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" } } } },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Flag?" }]
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "strike-1", Input = "text", SafetyIdentifier = "unsupported",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Flag?" }]
        }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public async Task Enforces_choice_limits(int count)
    {
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionChoiceQuestion
        {
            Instructions = "Pick.", Criteria = Enumerable.Range(0, count).ToDictionary(index => index.ToString(), _ => (DecisionInput?)null)
        };
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public async Task Enforces_score_limits(int count)
    {
        var request = BooleanRequest();
        request.Questions["flag"] = new DecisionScoreQuestion
        {
            Instructions = "Rate.", Criteria = Enumerable.Range(0, count).Select(index => (DecisionInput?)new DecisionInput(index.ToString())).ToArray()
        };
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Enforces_question_limit()
    {
        var request = BooleanRequest();
        request.Questions = Enumerable.Range(0, 33).ToDictionary(index => index.ToString(),
            _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "Flag?" });
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Accepts_documented_maximum_questions_choices_and_levels()
    {
        var request = BooleanRequest();
        request.Questions = Enumerable.Range(0, 30).ToDictionary(index => index.ToString(),
            _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "Flag?" });
        request.Questions["route"] = new DecisionChoiceQuestion
        {
            Instructions = "Pick.", Criteria = Enumerable.Range(0, 255).ToDictionary(index => index.ToString(), _ => (DecisionInput?)null)
        };
        request.Questions["score"] = new DecisionScoreQuestion
        {
            Instructions = "Rate.", Criteria = Enumerable.Range(0, 10).Select(index => (DecisionInput?)new DecisionInput(index.ToString())).ToArray()
        };
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(32, document.RootElement.GetProperty("questions").EnumerateObject().Count());
            var answers = Enumerable.Range(0, 30).ToDictionary(index => index.ToString(),
                _ => (object)new { type = "noul", noul = 0.9 });
            answers["route"] = new { type = "choice", choice = "0", confidence = 1,
                probabilities = Enumerable.Range(0, 255).ToDictionary(index => index.ToString(), index => index == 0 ? 1d : 0d) };
            answers["score"] = new { type = "score", score = 9, confidence = 1,
                probabilities = Enumerable.Range(0, 10).ToDictionary(index => index.ToString(), index => index == 9 ? 1d : 0d) };
            return Success(JsonSerializer.Serialize(answers));
        });
        Assert.Equal(32, (await provider.DecisionRequestAsync(request)).Answers.Count);
    }

    [Fact]
    public async Task Normalizes_sdk_text_and_json_without_mutating_the_request()
    {
        var request = BooleanRequest();
        request.StateFormat = "sdk";
        request.State = JsonSerializer.Deserialize<JsonElement>("""[{"type":"text","text":"evidence"},{"type":"json","value":{"ticket":1}}]""");
        var originalState = request.State;
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var content = document.RootElement.GetProperty("state")[0].GetProperty("content");
            Assert.Equal("evidence", content[0].GetProperty("text").GetString());
            Assert.Equal("{\"ticket\":1}", content[1].GetProperty("text").GetString());
            return Success("""{"flag":{"type":"noul","noul":0.9}}""");
        });
        await provider.DecisionRequestAsync(request);
        Assert.Same(originalState, request.State);
        Assert.Equal("sdk", request.StateFormat);
    }

    [Theory]
    [InlineData("""{"id":"req","model":"strike-1","answers":{"flag":{"type":"noul","noul":0.9}},"usage":{"input_tokens":-1,"output_tokens":1}}""")]
    [InlineData("""{"id":"","model":"strike-1","answers":{"flag":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"id":"req","model":"","answers":{"flag":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"id":"req","model":"strike-1","answers":{"flag":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1}}""")]
    [InlineData("""{"id":"req","model":"strike-1","answers":{"flag":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1.5,"output_tokens":1}}""")]
    [InlineData("not json")]
    public async Task Rejects_invalid_response_envelopes(string raw)
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create((_, _) => Task.FromResult(Json(raw))).DecisionRequestAsync(BooleanRequest()));

    [Fact]
    public async Task OpenAI_text_messages_are_preserved_as_structured_state()
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var state = document.RootElement.GetProperty("state");
            Assert.Equal("user", state[0].GetProperty("role").GetString());
            Assert.Equal("text evidence", state[0].GetProperty("content").GetString());
            return Success("""{"question-0":{"type":"noul","noul":0.9}}""");
        });
        await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "strike-1", Input = new OpenAIDecisionInputMessage[] { new() { Content = "text evidence" } },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Flag?" }]
        });
    }

    [Fact]
    public async Task Does_not_advertise_unrelated_inference_capabilities()
    {
        var provider = NoSend();
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CompleteChatAsync(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.EmbeddingRequestAsync(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new()));
    }

    [Fact]
    public async Task Requires_credentials_and_honors_preflight_and_inflight_cancellation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create((_, _) => throw new InvalidOperationException("Must not send."), "").DecisionRequestAsync(BooleanRequest()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoSend().DecisionRequestAsync(BooleanRequest(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoSend().ListModels(cancelled.Token));
        using var inflight = new CancellationTokenSource();
        var provider = Create((_, ct) =>
        {
            inflight.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Must cancel.");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DecisionRequestAsync(BooleanRequest(), inflight.Token));
    }

    [Fact]
    public void Registration_headers_and_static_catalog_use_the_same_identifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IApiKeyResolver>(new KeyResolver("strike-test-key"));
        services.AddSingleton<IHttpClientFactory>(new ClientFactory(new HttpClient()));
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(StrikeAIProvider));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("strikeai"));
        Assert.IsType<StrikeAIProvider>(registry.GetModelProvider(scope.ServiceProvider, "strikeai"));
        Assert.Equal("X-StrikeAI-Key", HeaderApiKeyResolver.SupportedProviderHeaders["strikeai"]);
        Assert.True(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("idempotency-key", "StrikeAI"));
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("Idempotency-Key", "openai"));
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("X-StrikeAI-Key", "strikeai"));
        var model = Assert.Single("strikeai".GetModels());
        Assert.Equal("strikeai/strike-1", model.Id);
        Assert.Equal("decision", model.Type);
        Assert.Equal(8192, model.ContextWindow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_gateway_controllers_route_both_contracts_with_scoped_header_auth(bool openAI)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-StrikeAI-Key"] = "request-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        var provider = new StrikeAIProvider(resolver, new ClientFactory(new HttpClient(new Handler(async (http, ct) =>
        {
            Assert.Equal("request-key", http.Headers.Authorization!.Parameter);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("strike-1", body.RootElement.GetProperty("model").GetString());
            return Success(openAI ? """{"question-0":{"type":"noul","noul":0.9}}""" : """{"flag":{"type":"noul","noul":0.9}}""");
        }))), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
        var modelResolver = new ModelResolver(provider);
        var controllerContext = new ControllerContext { HttpContext = context };
        if (openAI)
        {
            var controller = new OpenAIDecisionsController(modelResolver) { ControllerContext = controllerContext };
            var action = await controller.Post(new()
            {
                Model = "strikeai/strike-1", Input = "text",
                Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Flag?" }]
            }, CancellationToken.None);
            Assert.IsType<OpenAIDecisionResponse>(Assert.IsType<OkObjectResult>(action).Value);
        }
        else
        {
            var controller = new DecisionsController(modelResolver) { ControllerContext = controllerContext };
            var action = await controller.Post(BooleanRequest(), CancellationToken.None);
            Assert.IsType<DecisionResponse>(Assert.IsType<OkObjectResult>(action).Value);
        }
    }

    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "strikeai/strike-1", State = "text",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Flag?" } }
    };

    private static StrikeAIProvider NoSend()
        => Create((_, _) => throw new InvalidOperationException("Request must not be sent."));

    private static StrikeAIProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "strike-test-key")
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage Success(string answers)
        => Json("{\"id\":\"req_1\",\"model\":\"strike-1\",\"answers\":" + answers
            + ",\"usage\":{\"input_tokens\":12,\"output_tokens\":4,\"cost_eur\":\"0.000123\",\"cost_currency\":\"EUR\"},"
            + "\"meta\":{\"latency_ms\":42}}");

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "strikeai" ? key : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ModelResolver(IModelProvider provider) : IAIModelProviderResolver
    {
        public Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
        {
            Assert.Equal("strikeai/strike-1", model);
            return Task.FromResult(provider);
        }
        public IModelProvider GetProvider() => provider;
        public Task<ModelResponse> ResolveModels(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
