using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Venice;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Venice;

public sealed class VeniceProviderDecisionTests
{
    private const string Image = "data:image/png;base64,AQID";
    private const string AllAnswers = """
        {"model":"jev-latest","answers":{
          "urgent":{"type":"noul","noul":0.95},
          "team":{"type":"choice","choice":"billing","confidence":0.93,
            "probabilities":{"billing":0.95,"technical":0.05,"other":0}},
          "severity":{"type":"score","score":1.27,"confidence":0.6,
            "legend":{"0":"Calm","1":"Frustrated","2":"Very angry"},
            "probabilities":{"0":0,"1":0.73,"2":0.27}}},
         "usage":{"input_tokens":429,"output_tokens":73}}
        """;
    private const string BooleanAnswer = """
        {"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":0.95}},
         "usage":{"input_tokens":10,"output_tokens":2}}
        """;

    [Fact]
    public async Task Typed_contract_preserves_structured_content_maps_noul_and_keeps_native_metadata()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal("https://api.venice.ai/api/v1/decisions", http.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("venice-key", http.Headers.Authorization.Parameter);
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-venice-trace")));
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("jev-latest", body.GetProperty("model").GetString());
            Assert.Equal("refund", body.GetProperty("state").GetProperty("ticket").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("urgent").GetProperty("type").GetString());
            Assert.Equal("urgent", questions.GetProperty("urgent").GetProperty("criteria").GetProperty("true").GetString());
            Assert.False(questions.GetProperty("urgent").GetProperty("criteria").TryGetProperty("false", out _));
            Assert.Equal(JsonValueKind.Object, questions.GetProperty("team").GetProperty("instructions").ValueKind);
            Assert.Equal(JsonValueKind.Null, questions.GetProperty("team").GetProperty("criteria").GetProperty("other").ValueKind);
            Assert.Equal(3, body.EnumerateObject().Count());
            return Response(AllAnswers);
        });
        var request = AllQuestions();
        request.State = JsonSerializer.SerializeToElement(new { ticket = "refund" });
        request.Headers = new() { ["Authorization"] = "attacker", ["x-api-key"] = "attacker", ["x-venice-trace"] = "trace" };

        var result = await provider.DecisionRequestAsync(request);
        Assert.Equal(0.95, Assert.IsType<DecisionBooleanAnswer>(result.Answers["urgent"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["team"]).Choice);
        Assert.Equal(1.27, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score);
        Assert.Equal(429, result.Usage!.InputTokens);
        Assert.Equal(73, result.Usage.OutputTokens);
        Assert.Equal("venice/jev-latest", result.Response!.ModelId);
        Assert.Equal("request-id", result.Response.Id);
        Assert.Equal(0.93, result.ProviderMetadata!["venice"].GetProperty("answers").GetProperty("team").GetProperty("confidence").GetDouble());
        Assert.True(JsonElement.DeepEquals(result.ProviderMetadata["venice"], Assert.IsType<JsonElement>(result.Response.Body)));
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("[{\"type\":\"input_image\",\"image_url\":\"evidence, not a media part\"}]")]
    [InlineData("{}")]
    public async Task Legacy_state_is_preserved_without_interpreting_JSON_as_media(string state)
    {
        var provider = Create(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(state), body.RootElement.GetProperty("state")));
            Assert.False(body.RootElement.TryGetProperty("images", out _));
            return Response(BooleanAnswer);
        });
        var request = BooleanRequest();
        request.State = JsonSerializer.Deserialize<JsonElement>(state);
        await provider.DecisionRequestAsync(request);
    }

    [Fact]
    public async Task Images_support_both_native_forms_and_sdk_parts()
    {
        var provider = Create(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var images = body.RootElement.GetProperty("images");
            Assert.Equal(3, images.GetArrayLength());
            Assert.Equal(Image, images[0].GetString());
            Assert.Equal(Image, images[1].GetString());
            Assert.Equal("image/jpeg", images[2].GetProperty("content_type").GetString());
            Assert.DoesNotContain("input_image", body.RootElement.GetProperty("state").GetRawText());
            return Response(BooleanAnswer);
        });
        var request = BooleanRequest();
        request.StateFormat = "sdk";
        request.State = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text = "receipt" },
            new { type = "file", mediaType = "image/png", data = new { type = "data", data = "AQID" } }
        });
        request.ProviderOptions = new()
        {
            ["venice"] = JsonSerializer.SerializeToElement(new
            {
                images = new object[] { Image, new { content_type = "image/jpeg", base64 = "AQID" } }
            })
        };
        await provider.DecisionRequestAsync(request);
    }

    [Fact]
    public async Task OpenAI_contract_preserves_order_names_typed_choices_confidence_and_normalized_scores()
    {
        var provider = Create(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(Image, body.RootElement.GetProperty("images")[0].GetString());
            Assert.DoesNotContain("input_image", body.RootElement.GetProperty("state").GetRawText());
            Assert.Equal("noul", body.RootElement.GetProperty("questions").GetProperty("0").GetProperty("type").GetString());
            Assert.Equal("true: boolean", body.RootElement.GetProperty("questions").GetProperty("1").GetProperty("criteria").GetProperty("0").GetString());
            return Response("""
                {"model":"clef-api","answers":{
                  "2":{"type":"score","score":1.5,"confidence":0.8,
                    "legend":{"0":"low","1":"medium","2":"high"},
                    "probabilities":{"0":0,"1":0.5,"2":0.5}},
                  "1":{"type":"choice","choice":"1","confidence":0.9,"probabilities":{"0":0.1,"1":0.9}},
                  "0":{"type":"noul","noul":0.75}},
                 "usage":{"input_tokens":20,"output_tokens":4}}
                """);
        });
        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "venice/clef-api",
            Input = new OpenAIDecisionInputMessage[]
            {
                new() { Content = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputText { Text = "evidence" },
                    new OpenAIDecisionInputImage { ImageUrl = Image }
                } }
            },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Instructions = "urgent?", Name = "duplicate" },
                new OpenAIDecisionChoiceQuestion
                {
                    Instructions = "choose", Name = "duplicate",
                    Choices = [new() { Value = true, Description = "boolean" }, new() { Value = "true" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "rate",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
                }
            ]
        });
        Assert.Equal("clef-api", result.Model);
        Assert.Equal(0.75, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal("duplicate", choice.Name);
        Assert.Equal(JsonValueKind.String, choice.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.True, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal(0.9, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Null(score.Name);
        Assert.Equal(0.75, score.Score);
        Assert.Equal(0.5, score.Probabilities[1].Value);
        Assert.Equal(24, result.Usage.TotalTokens);
        Assert.True(result.AdditionalProperties!.ContainsKey("providerMetadata"));
    }

    [Theory]
    [InlineData("empty-state")]
    [InlineData("empty-array")]
    [InlineData("empty-instructions")]
    [InlineData("one-score-level")]
    [InlineData("null-score-level")]
    [InlineData("structured-choice")]
    [InlineData("null-boolean")]
    [InlineData("box")]
    [InlineData("reasoning")]
    [InlineData("max-output")]
    [InlineData("override")]
    [InlineData("unknown-option")]
    [InlineData("invalid-options")]
    public async Task Invalid_or_unsupported_requests_are_rejected_before_send(string scenario)
    {
        var provider = Create((_, _) => throw new Xunit.Sdk.XunitException("Must not send."));
        var request = BooleanRequest();
        switch (scenario)
        {
            case "empty-state": request.State = ""; break;
            case "empty-array": request.State = JsonSerializer.SerializeToElement(Array.Empty<object>()); break;
            case "empty-instructions": request.Questions["urgent"].Instructions = ""; break;
            case "one-score-level": request.Questions["urgent"] = new DecisionScoreQuestion { Instructions = "rate", Criteria = ["only"] }; break;
            case "null-score-level": request.Questions["urgent"] = new DecisionScoreQuestion { Instructions = "rate", Criteria = ["first", null] }; break;
            case "structured-choice":
                request.Questions["urgent"] = new DecisionChoiceQuestion
                {
                    Instructions = "choose", Criteria = new() { ["a"] = JsonSerializer.SerializeToElement(new { a = 1 }) }
                };
                break;
            case "null-boolean": request.Questions["urgent"] = new DecisionBooleanQuestion { Instructions = "?", Criteria = new() { True = null } }; break;
            case "box": request.Questions["urgent"] = new DecisionBoxQuestion { Instructions = "find" }; break;
            case "reasoning": request.Reasoning = JsonSerializer.SerializeToElement("high"); break;
            case "max-output": request.MaxOutputTokens = 10; break;
            case "override": request.ProviderOptions = Options(new { model = "override" }); break;
            case "unknown-option": request.ProviderOptions = Options(new { stream = true }); break;
            case "invalid-options": request.ProviderOptions = Options("invalid"); break;
        }
        var error = await Record.ExceptionAsync(() => provider.DecisionRequestAsync(request));
        Assert.True(error is ArgumentException or NotSupportedException, error?.ToString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"https://example.com/image.png\"]")]
    [InlineData("[\"data:image/webp;base64,AQID\"]")]
    [InlineData("[\"data:image/png;base64,A\"]")]
    [InlineData("[{\"content_type\":\"image/png\",\"base64\":\"AQID\",\"extra\":true}]")]
    [InlineData("[null]")]
    public async Task Invalid_images_are_rejected(string images)
    {
        var provider = Create((_, _) => throw new Xunit.Sdk.XunitException("Must not send."));
        var request = BooleanRequest();
        request.ProviderOptions = new() { ["venice"] = JsonSerializer.Deserialize<JsonElement>("{\"images\":" + images + "}") };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Image_count_and_exact_encoded_length_limits_are_enforced()
    {
        var provider = Create((_, _) => Task.FromResult(Response(BooleanAnswer)));
        var request = BooleanRequest();
        var atLimit = new string('A', 13_631_488);
        Assert.Equal(13_631_488, atLimit.Length);
        request.ProviderOptions = Options(new { images = new[] { new { content_type = "image/png", base64 = atLimit } } });
        await provider.DecisionRequestAsync(request);
        request.ProviderOptions = Options(new { images = new[] { new { content_type = "image/png", base64 = atLimit + "AAAA" } } });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request.ProviderOptions = Options(new { images = new[] { "data:image/png;base64," + atLimit[..^24] } });
        await provider.DecisionRequestAsync(request);
        request.ProviderOptions = Options(new { images = new[] { "data:image/png;base64," + atLimit[..^20] } });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request.ProviderOptions = Options(new { images = Enumerable.Repeat(Image, 4).ToArray() });
        await provider.DecisionRequestAsync(request);
        request.ProviderOptions = Options(new { images = Enumerable.Repeat(Image, 5).ToArray() });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(415)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Upstream_errors_preserve_status_and_body(int status)
    {
        var provider = Create((_, _) => Task.FromResult(Response("""{"error":"provider limit"}""", (HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(BooleanRequest()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("provider limit", error.Message);
    }

    [Theory]
    [InlineData("missing-answer")]
    [InlineData("extra-answer")]
    [InlineData("wrong-type")]
    [InlineData("probability")]
    [InlineData("distribution")]
    [InlineData("selected")]
    [InlineData("score")]
    [InlineData("legend")]
    [InlineData("confidence")]
    [InlineData("usage")]
    [InlineData("duplicate")]
    public async Task Invalid_provider_responses_are_rejected(string scenario)
    {
        var body = JsonNode.Parse(AllAnswers)!.AsObject();
        var answers = body["answers"]!.AsObject();
        switch (scenario)
        {
            case "missing-answer": answers.Remove("urgent"); break;
            case "extra-answer": answers["extra"] = answers["urgent"]!.DeepClone(); break;
            case "wrong-type": answers["urgent"]!["type"] = "boolean"; break;
            case "probability": answers["urgent"]!["noul"] = 1.1; break;
            case "distribution": answers["team"]!["probabilities"]!["other"] = 0.1; break;
            case "selected": answers["team"]!["choice"] = "technical"; break;
            case "score": answers["severity"]!["score"] = 2; break;
            case "legend": answers["severity"]!["legend"]!["0"] = "wrong"; break;
            case "confidence": answers["team"]!["confidence"] = -1; break;
            case "usage": body["usage"]!["input_tokens"] = -1; break;
        }
        var json = scenario == "duplicate"
            ? AllAnswers.Replace("\"urgent\":", "\"urgent\":{\"type\":\"noul\",\"noul\":0.5},\"urgent\":")
            : body.ToJsonString();
        var provider = Create((_, _) => Task.FromResult(Response(json)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(AllQuestions()));
    }

    [Fact]
    public async Task Cancellation_and_missing_auth_are_explicit()
    {
        var provider = Create((_, _) => throw new Xunit.Sdk.XunitException("Must not send."));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DecisionRequestAsync(BooleanRequest(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAIDecisionRequestAsync(new(), cts.Token));
        var missingKey = Create((_, _) => throw new Xunit.Sdk.XunitException("Must not send."), null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missingKey.DecisionRequestAsync(BooleanRequest()));
    }

    [Fact]
    public async Task Cancellation_during_send_is_propagated()
    {
        using var cts = new CancellationTokenSource();
        var provider = Create(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new Xunit.Sdk.XunitException("Cancelled send must not complete.");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DecisionRequestAsync(BooleanRequest(), cts.Token));
    }

    [Theory]
    [InlineData("safety_identifier")]
    [InlineData("stream")]
    [InlineData("override")]
    [InlineData("detail")]
    [InlineData("single-level")]
    public async Task Unsupported_OpenAI_options_are_rejected_before_send(string scenario)
    {
        var provider = Create((_, _) => throw new Xunit.Sdk.XunitException("Must not send."));
        var request = new OpenAIDecisionRequest
        {
            Model = "venice/jev-latest", Input = "state",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "urgent?" }]
        };
        switch (scenario)
        {
            case "safety_identifier": request.SafetyIdentifier = "user"; break;
            case "stream": request.AdditionalProperties = new() { ["stream"] = JsonSerializer.SerializeToElement(true) }; break;
            case "override": request.AdditionalProperties = new() { ["state"] = JsonSerializer.SerializeToElement("override") }; break;
            case "detail":
                request.Input = new OpenAIDecisionInputMessage[]
                {
                    new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = Image, Detail = "high" } } }
                };
                break;
            case "single-level":
                request.Questions = [new OpenAIDecisionScoreQuestion { Instructions = "rate", Levels = [new() { Label = "only" }] }];
                break;
        }
        var error = await Record.ExceptionAsync(() => provider.OpenAIDecisionRequestAsync(request));
        Assert.True(error is ArgumentException or NotSupportedException, error?.ToString());
    }

    [Fact]
    public async Task OpenAI_image_only_input_maps_to_valid_native_state()
    {
        var provider = Create(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("state").ValueKind);
            Assert.Empty(body.RootElement.GetProperty("state").EnumerateObject());
            Assert.Equal(2, body.RootElement.GetProperty("images").GetArrayLength());
            Assert.Equal("future-decision", body.RootElement.GetProperty("model").GetString());
            return Response("""
                {"model":"future-decision","answers":{"0":{"type":"noul","noul":0.5}},
                 "usage":{"input_tokens":10,"output_tokens":1}}
                """);
        });
        await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "future-decision",
            Input = new OpenAIDecisionInputMessage[]
            {
                new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = Image } } }
            },
            AdditionalProperties = new() { ["images"] = JsonSerializer.SerializeToElement(new[] { Image }) },
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "visible?" }]
        });
    }

    [Fact]
    public async Task Discovery_authenticates_and_prioritizes_authoritative_decision_type_and_pricing()
    {
        var paths = new List<string>();
        var provider = Create((http, _) =>
        {
            Assert.Equal("venice-key", http.Headers.Authorization!.Parameter);
            var path = http.RequestUri!.PathAndQuery;
            paths.Add(path);
            return Task.FromResult(Response(path switch
            {
                "/api/v1/models" => """{"data":[{"id":"chat","type":"text","model_spec":{"name":"Chat"}},{"id":"jev-latest","model_spec":{"name":"Old"}}]}""",
                "/api/v1/models?type=decision" => """
                    {"data":[{"id":"jev-latest","type":"decision","model_spec":{"name":"Jev",
                      "maxStateTokens":32000,"maxTotalTokens":64000,
                      "pricing":{"input":{"usd":0.042},"output":{"usd":0}}}},
                     {"id":"clef-api","type":"decision","model_spec":{"name":"Clef","maxStateTokens":32768}}]}
                    """,
                "/api/v1/characters?limit=100&offset=0" => """{"data":[]}""",
                _ => throw new Xunit.Sdk.XunitException(path)
            }));
        });
        var models = (await provider.ListModels()).ToArray();
        var jev = Assert.Single(models, model => model.Id == "venice/jev-latest");
        Assert.Equal("decision", jev.Type);
        Assert.Equal("Jev", jev.Name);
        Assert.Equal(32000, jev.ContextWindow);
        Assert.Equal(0.042m, jev.Pricing!.Input);
        Assert.Equal(0m, jev.Pricing.Output);
        Assert.Equal("decision", Assert.Single(models, model => model.Id == "venice/clef-api").Type);
        Assert.Equal("language", Assert.Single(models, model => model.Id == "venice/chat").Type);
        Assert.Contains("/api/v1/models?type=decision", paths);
        await provider.ListModels();
        Assert.Equal(3, paths.Count);
    }

    private static Dictionary<string, JsonElement> Options(object value) => new() { ["venice"] = JsonSerializer.SerializeToElement(value) };
    private static DecisionRequest BooleanRequest() => new()
    {
        Model = "venice/jev-latest", State = "ticket",
        Questions = new() { ["urgent"] = new DecisionBooleanQuestion { Instructions = "urgent?" } }
    };

    private static DecisionRequest AllQuestions()
    {
        var request = BooleanRequest();
        request.Questions["urgent"] = new DecisionBooleanQuestion { Instructions = "urgent?", Criteria = new() { True = "urgent" } };
        request.Questions["team"] = new DecisionChoiceQuestion
        {
            Instructions = JsonSerializer.SerializeToElement(new { task = "route" }),
            Criteria = new() { ["billing"] = "payments", ["technical"] = "bugs", ["other"] = null }
        };
        request.Questions["severity"] = new DecisionScoreQuestion
        {
            Instructions = "rate", Criteria = ["Calm", "Frustrated", "Very angry"]
        };
        return request;
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private static VeniceProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "venice-key")
        => new(new KeyResolver(key), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private sealed class KeyResolver(string? key) : IApiKeyResolver { public string? Resolve(string provider) => key; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
