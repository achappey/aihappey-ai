using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.InceptionLabs;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.InceptionLabs;

public sealed class InceptionLabsProviderDecisionTests
{
    private const string ImageUrl = "data:image/png;base64,AQID";

    [Fact]
    public async Task Official_response_example_maps_fractional_scores_and_native_metadata()
    {
        var provider = Create((_, _) => Task.FromResult(Response("""
            {
              "model":"mercury-decide",
              "answers":{
                "billing":{"type":"noul","noul":0.97},
                "team":{"type":"choice","choice":"billing",
                  "probabilities":{"billing":0.96,"support":0.04},"confidence":0.96},
                "urgency":{"type":"score","score":1.1,
                  "legend":{"0":"Can wait","1":"Needs attention soon","2":"Needs immediate attention"},
                  "probabilities":{"0":0.1,"1":0.7,"2":0.2},"confidence":0.7}
              },
              "usage":{"input_tokens":120,"output_tokens":3}
            }
            """)));
        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "mercury-decide",
            State = Json("""{"message":"Please refund the duplicate charge on my account."}"""),
            Questions = new()
            {
                ["billing"] = new DecisionBooleanQuestion { Instructions = "Is this request about billing?" },
                ["team"] = new DecisionChoiceQuestion
                {
                    Criteria = new() { ["billing"] = "Payments, charges, and refunds", ["support"] = "Technical support and other requests" }
                },
                ["urgency"] = new DecisionScoreQuestion
                {
                    Instructions = "How urgent is this request?",
                    Criteria = ["Can wait", "Needs attention soon", "Needs immediate attention"]
                }
            }
        });
        Assert.Equal(0.97, Assert.IsType<DecisionBooleanAnswer>(response.Answers["billing"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(response.Answers["team"]).Choice);
        var score = Assert.IsType<DecisionScoreAnswer>(response.Answers["urgency"]);
        Assert.Equal(1.1, score.Score);
        Assert.Equal(0.7, score.Probabilities!["1"]);
        Assert.Equal(120, response.Usage!.InputTokens);
        Assert.Equal(3, response.Usage.OutputTokens);
        Assert.Equal(0.7, response.ProviderMetadata!["inceptionlabs"].GetProperty("answers").GetProperty("urgency").GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task Native_contract_preserves_structured_questions_and_maps_all_answers()
    {
        var state = Json("""{"ticket":{"text":"refund","amount":42},"history":["charged twice"]}""");
        var levels = new DecisionInput?[] { "later", Json("""{"label":"today","hours":8}"""), Json("""["now", "urgent"]""") };
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://api.inceptionlabs.ai/v1/decisions", http.RequestUri!.ToString());
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("inception-key", http.Headers.Authorization.Parameter);
            Assert.False(http.Headers.Contains("x-api-key"));
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-inceptionlabs-trace")));
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal(3, body.EnumerateObject().Count());
            Assert.Equal("mercury-decide", body.GetProperty("model").GetString());
            Assert.True(JsonElement.DeepEquals(state, body.GetProperty("state")));
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("billing").GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, questions.GetProperty("billing").GetProperty("criteria").GetProperty("false").ValueKind);
            Assert.False(questions.GetProperty("billing").TryGetProperty("instructions", out _));
            Assert.False(questions.GetProperty("team").TryGetProperty("instructions", out _));
            Assert.Equal(2, questions.GetProperty("team").GetProperty("criteria").GetProperty("payments").GetProperty("priority").GetInt32());
            Assert.Equal(JsonValueKind.Null, questions.GetProperty("team").GetProperty("criteria").GetProperty("other").ValueKind);
            Assert.Equal("context", questions.GetProperty("urgency").GetProperty("instructions")[0].GetString());
            var response = Success(body);
            response.Headers.Add("x-request-id", "decision-id");
            return response;
        });
        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "inceptionlabs/mercury-decide", State = state,
            Headers = new() { ["Authorization"] = "attacker", ["x-api-key"] = "attacker", ["x-inceptionlabs-trace"] = "trace" },
            Questions = new()
            {
                ["billing"] = new DecisionBooleanQuestion { Criteria = new() { True = "billing", False = null } },
                ["team"] = new DecisionChoiceQuestion
                {
                    Criteria = new() { ["payments"] = Json("""{"priority":2}"""), ["other"] = null }
                },
                ["urgency"] = new DecisionScoreQuestion { Instructions = Json("""["context", {"question":"urgent?"}]"""), Criteria = levels }
            }
        });
        Assert.Equal(0.75, Assert.IsType<DecisionBooleanAnswer>(response.Answers["billing"]).Probability);
        Assert.Equal("payments", Assert.IsType<DecisionChoiceAnswer>(response.Answers["team"]).Choice);
        Assert.Equal(1, Assert.IsType<DecisionScoreAnswer>(response.Answers["urgency"]).Score);
        Assert.Equal(12, response.Usage!.InputTokens);
        Assert.Equal(3, response.Usage.OutputTokens);
        Assert.Equal("decision-id", response.Response!.Id);
        Assert.Equal("inceptionlabs/mercury-decide", response.Response.ModelId);
        var raw = response.ProviderMetadata!["inceptionlabs"];
        Assert.Equal(0.5, raw.GetProperty("answers").GetProperty("team").GetProperty("confidence").GetDouble());
        Assert.True(JsonElement.DeepEquals(raw, Assert.IsType<JsonElement>(response.Response.Body)));
        Assert.Contains(response.Response.Headers!, item => item.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Openai_mapping_preserves_order_names_typed_choices_and_images()
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            var content = body.GetProperty("state")[0].GetProperty("content");
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal(ImageUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
            var criteria = body.GetProperty("questions").GetProperty("question-1").GetProperty("criteria");
            Assert.Equal(JsonValueKind.True, criteria.GetProperty("option-0").GetProperty("value").ValueKind);
            Assert.Equal("true", criteria.GetProperty("option-1").GetProperty("value").GetString());
            return Success(body);
        });
        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "inceptionlabs/mercury-decide",
            Input = new OpenAIDecisionInputMessage[]
            {
                new() { Content = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputText { Text = "Inspect" },
                    new OpenAIDecisionInputImage { ImageUrl = ImageUrl, Detail = "auto" }
                } }
            },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Instructions = "Is it valid?", Name = "duplicate" },
                new OpenAIDecisionChoiceQuestion
                {
                    Instructions = "Choose", Name = "duplicate",
                    Choices = [new() { Value = true }, new() { Value = "true", Description = "text" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "Rate", Levels = [new() { Label = "low" }, new() { Label = "high", Description = "urgent" }]
                }
            ]
        });
        Assert.Equal("mercury-decide", response.Model);
        Assert.Equal(0.75, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        Assert.Equal("duplicate", response.Answers[0].Name);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]);
        Assert.Equal("duplicate", choice.Name);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[1].Value.Value.ValueKind);
        Assert.Equal(0.5, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]);
        Assert.Null(score.Name);
        Assert.Equal(0.5, score.Score);
        Assert.Equal("high", score.Probabilities[1].Label);
        Assert.Equal(1, score.Probabilities[1].Value);
        Assert.Equal(15, response.Usage.TotalTokens);
        Assert.Equal(0, response.Usage.InputTokensDetails.CachedTokens);
        Assert.True(response.AdditionalProperties!.ContainsKey("providerMetadata"));
        Assert.True(response.AdditionalProperties.ContainsKey("warnings"));
    }

    [Fact]
    public async Task Sdk_parts_reuse_state_mapping_and_translate_images()
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var parts = document.RootElement.GetProperty("state")[0].GetProperty("content");
            Assert.Equal("text", parts[0].GetProperty("type").GetString());
            Assert.Equal("""{"a":1}""", parts[1].GetProperty("text").GetString());
            Assert.Equal("File: notes.txt", parts[2].GetProperty("text").GetString());
            Assert.Equal("notes", parts[3].GetProperty("text").GetString());
            Assert.Equal(ImageUrl, parts[4].GetProperty("image_url").GetProperty("url").GetString());
            return Success(document.RootElement);
        });
        var request = Request();
        request.StateFormat = "sdk";
        request.State = Json("""
            [{"type":"text","text":"inspect"},{"type":"json","value":{"a":1}},
             {"type":"file","filename":"notes.txt","mediaType":"text/plain","data":{"type":"text","text":"notes"}},
             {"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQID"}}]
            """);
        await provider.DecisionRequestAsync(request);
    }

    [Fact]
    public async Task Native_images_are_allowed_recursively_in_state_and_instructions()
    {
        var provider = Echo();
        var request = Request();
        request.State = JsonSerializer.SerializeToElement(new { nested = new { image = Image() } });
        request.Questions["flag"].Instructions = JsonSerializer.SerializeToElement(new { parts = new[] { Image() } });
        await provider.DecisionRequestAsync(request);
    }

    [Fact]
    public async Task Documented_option_and_level_boundaries_are_enforced()
    {
        var request = Request();
        request.Questions["choice"] = new DecisionChoiceQuestion
        {
            Criteria = Enumerable.Range(0, 255).ToDictionary(i => i.ToString(), _ => (DecisionInput?)null)
        };
        request.Questions["score"] = new DecisionScoreQuestion { Criteria = Enumerable.Repeat<DecisionInput?>("level", 10).ToArray() };
        await Echo().DecisionRequestAsync(request);
        ((DecisionChoiceQuestion)request.Questions["choice"]).Criteria["extra"] = null;
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
        request.Questions.Remove("choice");
        ((DecisionScoreQuestion)request.Questions["score"]).Criteria = Enumerable.Repeat<DecisionInput?>("level", 11).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
        ((DecisionScoreQuestion)request.Questions["score"]).Criteria = [null];
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
        ((DecisionScoreQuestion)request.Questions["score"]).Criteria = ["only"];
        request.Questions["choice"] = new DecisionChoiceQuestion { Criteria = new() { ["only"] = null } };
        await Echo().DecisionRequestAsync(request);
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,AQID", typeof(NotSupportedException))]
    [InlineData("https://example.com/image.png", typeof(NotSupportedException))]
    [InlineData("data:image/png;base64,invalid!", typeof(ArgumentException))]
    [InlineData("data:image/png;base64,", typeof(ArgumentException))]
    public async Task Invalid_images_are_rejected(string url, Type exception)
    {
        var request = Request();
        request.State = JsonSerializer.SerializeToElement(Image(url));
        await Assert.ThrowsAsync(exception, () => NoSend().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Image_count_is_request_wide_and_criteria_images_are_rejected()
    {
        var request = Request();
        request.State = JsonSerializer.SerializeToElement(Enumerable.Repeat(Image(), 16));
        await Echo().DecisionRequestAsync(request);
        request.Questions["flag"].Instructions = JsonSerializer.SerializeToElement(Image());
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
        request = Request();
        request.Questions["flag"] = new DecisionBooleanQuestion { Criteria = new() { True = JsonSerializer.SerializeToElement(Image()) } };
        await Assert.ThrowsAsync<NotSupportedException>(() => NoSend().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Image_size_and_actual_encoded_request_size_are_enforced()
    {
        var request = Request();
        var tenMiB = new byte[10 * 1024 * 1024];
        request.State = JsonSerializer.SerializeToElement(Image("data:image/png;base64," + Convert.ToBase64String(tenMiB)));
        await Echo().DecisionRequestAsync(request);
        request.State = JsonSerializer.SerializeToElement(Image("data:image/png;base64," + Convert.ToBase64String(new byte[tenMiB.Length + 1])));
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
        request.State = JsonSerializer.SerializeToElement(new[]
        {
            Image("data:image/png;base64," + Convert.ToBase64String(tenMiB)),
            Image("data:image/png;base64," + Convert.ToBase64String(tenMiB))
        });
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Serialized_request_accepts_exactly_twenty_MiB_but_not_one_byte_more()
    {
        const int limit = 20 * 1024 * 1024;
        const string empty = """{"model":"mercury-decide","state":"","questions":{"flag":{"type":"noul","instructions":"Is it true?"}}}""";
        var request = Request();
        request.State = new string('a', limit - Encoding.UTF8.GetByteCount(empty));
        var provider = Create((http, _) =>
        {
            Assert.Equal(limit, http.Content!.Headers.ContentLength);
            return Task.FromResult(Response("""
                {"model":"mercury-decide","answers":{"flag":{"type":"noul","noul":0.5}},
                 "usage":{"input_tokens":1,"output_tokens":1}}
                """));
        });
        await provider.DecisionRequestAsync(request);
        request.State = request.State.Value.GetString() + "a";
        await Assert.ThrowsAsync<ArgumentException>(() => NoSend().DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData("maxOutputTokens")]
    [InlineData("reasoning")]
    [InlineData("box")]
    [InlineData("providerOptions")]
    [InlineData("stream")]
    [InlineData("override")]
    [InlineData("audio")]
    [InlineData("detail")]
    [InlineData("model")]
    public async Task Unsupported_features_fail_before_http(string feature)
    {
        var request = Request();
        switch (feature)
        {
            case "maxOutputTokens": request.MaxOutputTokens = 1; break;
            case "reasoning": request.Reasoning = Json("{}"); break;
            case "box": request.Questions["box"] = new DecisionBoxQuestion { Instructions = "Locate" }; break;
            case "providerOptions": request.ProviderOptions = new() { ["inceptionlabs"] = Json("""{"extract":true}""") }; break;
            case "stream": request.ProviderOptions = new() { ["inceptionlabs"] = Json("""{"stream":true}""") }; break;
            case "override": request.ProviderOptions = new() { ["inceptionlabs"] = Json("""{"model":"other"}""") }; break;
            case "audio": request.State = Json("""{"type":"input_audio","input_audio":{"data":"AQID"}}"""); break;
            case "detail": request.State = JsonSerializer.SerializeToElement(new { type = "image_url", image_url = new { url = ImageUrl, detail = "high" } }); break;
            case "model": request.Model = "inceptionlabs/mercury-2"; break;
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => NoSend().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Invalid_requests_and_openai_options_are_rejected()
    {
        var provider = NoSend();
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DecisionRequestAsync(null!));
        var request = Request();
        request.State = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DecisionRequestAsync(request));
        request = Request();
        request.Questions.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request = Request();
        request.ProviderOptions = new() { ["inceptionlabs"] = Json("[]") };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        var openai = new OpenAIDecisionRequest
        {
            Model = "mercury-decide", Input = "state",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it true?" }],
            SafetyIdentifier = "user"
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(openai));
        openai.SafetyIdentifier = null;
        openai.AdditionalProperties = new() { ["stream"] = Json("true") };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(openai));
        openai.AdditionalProperties = null;
        openai.Input = new OpenAIDecisionInputMessage[] { new() { Content = new OpenAIDecisionInputPart[]
        {
            new OpenAIDecisionInputImage { ImageUrl = ImageUrl, Detail = "high" }
        } } };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(openai));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Upstream_errors_preserve_status_and_body(int status)
    {
        const string body = """{"error":{"code":"upstream_error","message":"service failed"}}""";
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(Request()));
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Contains(body, exception.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"model":"mercury-decide","answers":{},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"model":"mercury-decide","answers":{"flag":{"type":"noul","noul":1.1}},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"model":"mercury-decide","answers":{"flag":{"type":"noul","noul":0.5}},"usage":{"input_tokens":-1,"output_tokens":1}}""")]
    [InlineData("""{"model":"mercury-decide","answers":{"flag":{"type":"noul","noul":0.5}},"usage":{"input_tokens":2147483647,"output_tokens":1}}""")]
    public async Task Malformed_responses_fail_explicitly(string body)
    {
        var provider = Create((_, _) => Task.FromResult(Response(body)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(Request()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("unknownChoice")]
    [InlineData("nonmaximalChoice")]
    [InlineData("distributionSum")]
    [InlineData("distributionKey")]
    [InlineData("confidence")]
    [InlineData("weightedScore")]
    [InlineData("legend")]
    public async Task Invalid_answer_correspondence_and_distributions_are_rejected(string failure)
    {
        var request = Request();
        request.Questions["choice"] = new DecisionChoiceQuestion { Criteria = new() { ["a"] = null, ["b"] = null } };
        request.Questions["score"] = new DecisionScoreQuestion { Criteria = ["low", "high"] };
        var provider = Create(async (http, ct) =>
        {
            using var sent = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            using var response = Success(sent.RootElement);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (failure == "duplicate")
                return Response(raw.Replace("\"flag\":", "\"flag\":{\"type\":\"noul\",\"noul\":0.5},\"flag\":", StringComparison.Ordinal));
            var body = JsonNode.Parse(raw)!;
            var answers = body["answers"]!.AsObject();
            switch (failure)
            {
                case "missing": answers.Remove("flag"); break;
                case "extra": answers["extra"] = new JsonObject { ["type"] = "noul", ["noul"] = 0.5 }; break;
                case "type": answers["flag"]!["type"] = "choice"; break;
                case "unknownChoice": answers["choice"]!["choice"] = "c"; break;
                case "nonmaximalChoice":
                    answers["choice"]!["probabilities"]!["a"] = 0.1;
                    answers["choice"]!["probabilities"]!["b"] = 0.9;
                    break;
                case "distributionSum": answers["choice"]!["probabilities"]!["a"] = 0.6; break;
                case "distributionKey": answers["score"]!["probabilities"]!.AsObject().Remove("1"); break;
                case "confidence": answers["choice"]!["confidence"] = -0.1; break;
                case "weightedScore": answers["score"]!["score"] = 0.8; break;
                case "legend": answers["score"]!["legend"]!["1"] = "wrong"; break;
            }
            return Response(body.ToJsonString());
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData("""{"model":"mercury-decide","state":"state","questions":{"flag":{"type":"boolean"}}}""")]
    [InlineData("""{"model":"mercury-decide","state":["state"],"questions":{"flag":{"type":"boolean","criteria":{"true":null,"false":null}}}}""")]
    public async Task Optional_noul_instructions_are_not_fabricated(string json)
    {
        var request = JsonSerializer.Deserialize<DecisionRequest>(json)!;
        var provider = Create(async (http, ct) =>
        {
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.False(body.RootElement.GetProperty("questions").GetProperty("flag").TryGetProperty("instructions", out _));
            return Success(body.RootElement);
        });
        await provider.DecisionRequestAsync(request);
    }

    [Fact]
    public async Task Cancellation_and_missing_authentication_do_not_send_requests()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoSend().DecisionRequestAsync(Request(), cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create((_, _) => throw new Xunit.Sdk.XunitException("Must not send"), null)
            .DecisionRequestAsync(Request()));
        using var active = new CancellationTokenSource();
        var provider = Create((_, ct) =>
        {
            active.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new Xunit.Sdk.XunitException("Must cancel");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DecisionRequestAsync(Request(), active.Token));
    }

    [Fact]
    public async Task Catalog_discovery_includes_mercury_decide_and_existing_models()
    {
        var models = (await NoSend().ListModels()).ToArray();
        Assert.Contains(models, model => model.Id == "inceptionlabs/mercury-decide");
        Assert.Contains(models, model => model.Id == "inceptionlabs/mercury-2");
        Assert.Contains(models, model => model.Id == "inceptionlabs/mercury-edit");
    }

    private static DecisionRequest Request() => new()
    {
        Model = "mercury-decide", State = "evidence",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Is it true?" } }
    };

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static object Image(string url = ImageUrl) => new { type = "image_url", image_url = new { url } };
    private static InceptionLabsProvider NoSend() => Create((_, _) => throw new Xunit.Sdk.XunitException("Request must not be sent."));
    private static InceptionLabsProvider Echo() => Create(async (http, ct) =>
    {
        using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
        return Success(document.RootElement);
    });

    private static HttpResponseMessage Success(JsonElement request)
    {
        var answers = new JsonObject();
        foreach (var question in request.GetProperty("questions").EnumerateObject())
        {
            var type = question.Value.GetProperty("type").GetString();
            var answer = new JsonObject { ["type"] = type };
            if (type == "noul")
                answer["noul"] = 0.75;
            else
            {
                var criteria = question.Value.GetProperty("criteria");
                var keys = type == "choice" ? criteria.EnumerateObject().Select(item => item.Name).ToArray()
                    : Enumerable.Range(0, criteria.GetArrayLength()).Select(i => i.ToString()).ToArray();
                var probabilities = new JsonObject();
                foreach (var key in keys) probabilities[key] = 1.0 / keys.Length;
                answer["probabilities"] = probabilities;
                answer["confidence"] = 1.0 / keys.Length;
                if (type == "choice") answer["choice"] = keys[0];
                else
                {
                    answer["score"] = (keys.Length - 1) / 2.0;
                    var legend = new JsonObject();
                    for (var i = 0; i < keys.Length; i++) legend[keys[i]] = JsonNode.Parse(criteria[i].GetRawText());
                    answer["legend"] = legend;
                }
            }
            answers[question.Name] = answer;
        }
        return Response(new JsonObject
        {
            ["model"] = "mercury-decide", ["answers"] = answers,
            ["usage"] = new JsonObject { ["input_tokens"] = 12, ["output_tokens"] = 3 }
        }.ToJsonString());
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private static InceptionLabsProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "inception-key")
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(responder))));
    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
