using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.DigitalOcean;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.DigitalOcean;

public sealed class DigitalOceanProviderDecisionTests
{
    private const string Model = "typesafe-jev-1.13.0";
    private const string MixedResponse = """
        {
          "model":"typesafe-jev-1.13.0",
          "answers":{
            "followup":{"type":"noul","noul":0.68},
            "sentiment":{"type":"choice","choice":"mixed","confidence":1,
              "probabilities":{"positive":0,"negative":0,"mixed":1}},
            "satisfaction":{"type":"score","score":1.32,"confidence":0.52,
              "legend":{"0":"low","1":"medium","2":"high"},
              "probabilities":{"0":0,"1":0.68,"2":0.32}}
          },
          "usage":{"input_tokens":368,"output_tokens":59}
        }
        """;
    private const string BooleanResponse = """
        {"model":"typesafe-jev-1.13.0","answers":{"flag":{"type":"noul","noul":0.68}},
         "usage":{"input_tokens":307,"output_tokens":0}}
        """;

    [Fact]
    public async Task Mixed_questions_use_systemone_auth_and_shared_response_mapping()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://inference.do-ai.run/v1/systemone", http.RequestUri!.ToString());
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("digitalocean-key", http.Headers.Authorization.Parameter);
            Assert.False(http.Headers.Contains("x-api-key"));
            Assert.False(http.Headers.Contains("x-digitalocean-key"));
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-digitalocean-trace")));
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal(3, body.EnumerateObject().Count());
            Assert.Equal(Model, body.GetProperty("model").GetString());
            Assert.Equal("late delivery", body.GetProperty("state").GetProperty("feedback").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("followup").GetProperty("type").GetString());
            Assert.False(questions.GetProperty("followup").TryGetProperty("criteria", out _));
            Assert.Equal("both", questions.GetProperty("sentiment").GetProperty("criteria").GetProperty("mixed").GetString());
            Assert.Equal("medium", questions.GetProperty("satisfaction").GetProperty("criteria")[1].GetProperty("label").GetString());
            return Response(MixedResponse);
        });
        var request = new DecisionRequest
        {
            Model = $"digitalocean/{Model}",
            State = JsonSerializer.SerializeToElement(new { feedback = "late delivery" }),
            Headers = new()
            {
                ["Authorization"] = "attacker", ["x-api-key"] = "attacker",
                ["x-digitalocean-key"] = "attacker", ["x-digitalocean-trace"] = "trace"
            },
            Questions = new()
            {
                ["sentiment"] = new DecisionChoiceQuestion
                {
                    Instructions = "Classify sentiment.",
                    Criteria = new() { ["positive"] = "happy", ["negative"] = "unhappy", ["mixed"] = "both" }
                },
                ["satisfaction"] = new DecisionScoreQuestion
                {
                    Instructions = "Rate satisfaction.",
                    Criteria = ["low", JsonSerializer.SerializeToElement(new { label = "medium" }), "high"]
                },
                ["followup"] = new DecisionBooleanQuestion { Instructions = "Does this need follow-up?" }
            }
        };
        var result = await provider.DecisionRequestAsync(request);
        Assert.Equal(request.Questions.Keys, result.Answers.Keys);
        Assert.Equal(0.68, Assert.IsType<DecisionBooleanAnswer>(result.Answers["followup"]).Probability);
        var choice = Assert.IsType<DecisionChoiceAnswer>(result.Answers["sentiment"]);
        Assert.Equal("mixed", choice.Choice);
        Assert.Equal(1, choice.Probabilities!["mixed"]);
        var score = Assert.IsType<DecisionScoreAnswer>(result.Answers["satisfaction"]);
        Assert.Equal(1.32, score.Score, 6);
        Assert.Equal(0.68, score.Probabilities!["1"]);
        Assert.Equal(368, result.Usage!.InputTokens);
        Assert.Equal(59, result.Usage.OutputTokens);
        Assert.Equal($"digitalocean/{Model}", result.Response!.ModelId);
        Assert.Equal("request-id", result.Response.Id);
        Assert.NotNull(result.Response.Timestamp);
        Assert.Equal("request-id", result.Response.Headers!["x-request-id"]);
        var metadata = result.ProviderMetadata!["digitalocean"];
        Assert.Equal(0.52, metadata.GetProperty("answers").GetProperty("satisfaction").GetProperty("confidence").GetDouble());
        Assert.Equal("medium", metadata.GetProperty("answers").GetProperty("satisfaction").GetProperty("legend").GetProperty("1").GetString());
        Assert.True(JsonElement.DeepEquals(metadata, Assert.IsType<JsonElement>(result.Response.Body)));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("\"feedback\"")]
    [InlineData("[\"first\",\"second\"]")]
    [InlineData("{\"nested\":{\"count\":2},\"array\":[1,true,null]}")]
    public async Task Native_state_is_preserved_and_unprefixed_model_is_accepted(string state)
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(state), document.RootElement.GetProperty("state")));
            Assert.Equal(Model, document.RootElement.GetProperty("model").GetString());
            return Response(BooleanResponse);
        });
        var request = BooleanRequest();
        request.Model = Model;
        request.State = JsonSerializer.Deserialize<JsonElement>(state);
        var response = await provider.DecisionRequestAsync(request);
        Assert.Equal(0, response.Usage!.OutputTokens);
    }

    [Fact]
    public async Task Questions_are_not_subject_to_other_providers_count_limits()
    {
        var request = BooleanRequest();
        request.Questions = Enumerable.Range(0, 101).ToDictionary(index => $"flag{index}",
            _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "Follow up?" });
        var provider = Create((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new
        {
            model = Model,
            answers = request.Questions.ToDictionary(item => item.Key, _ => new { type = "noul", noul = 0.68 }),
            usage = new { input_tokens = 2000, output_tokens = 0 }
        }))));
        Assert.Equal(101, (await provider.DecisionRequestAsync(request)).Answers.Count);
    }

    [Fact]
    public async Task Sdk_text_json_and_text_file_parts_are_adapted_without_message_envelopes()
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var state = document.RootElement.GetProperty("state");
            Assert.Equal(new[] { "feedback", "{\"count\":2}", "File: note.txt", "file content" },
                state.EnumerateArray().Select(value => value.GetString()));
            return Response(BooleanResponse);
        });
        var request = BooleanRequest();
        request.StateFormat = "sdk";
        request.State = JsonSerializer.Deserialize<JsonElement>("""
            [{"type":"text","text":"feedback"},{"type":"json","value":{"count":2}},
             {"type":"file","mediaType":"text/plain","filename":"note.txt",
              "data":{"type":"text","text":"file content"}}]
            """);
        await provider.DecisionRequestAsync(request);
    }

    [Theory]
    [InlineData("stream", "true")]
    [InlineData("stream", "false")]
    [InlineData("model", "\"override\"")]
    [InlineData("state", "\"override\"")]
    [InlineData("questions", "{}")]
    [InlineData("temperature", "0")]
    [InlineData("safety_identifier", "\"user\"")]
    public async Task Provider_options_are_explicitly_rejected_before_http(string name, string value)
    {
        var request = BooleanRequest();
        request.ProviderOptions = new()
        {
            ["digitalocean"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                [name] = JsonSerializer.Deserialize<JsonElement>(value)
            })
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Unsupported_contract_features_and_invalid_requests_never_send_http()
    {
        var provider = NoHttp();
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = Model, Input = "feedback", SafetyIdentifier = "unsupported",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Follow up?" }]
        }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DecisionRequestAsync(null!));
        var request = BooleanRequest();
        request.MaxOutputTokens = 10;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Reasoning = JsonSerializer.SerializeToElement(new { effort = "high" });
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Questions["flag"] = new DecisionBoxQuestion { Instructions = "Locate." };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Questions["flag"] = new DecisionBooleanQuestion
        {
            Instructions = "Follow up?", Criteria = new() { True = "yes" }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Questions["flag"] = new DecisionChoiceQuestion
        {
            Instructions = "Choose.", Criteria = new() { ["a"] = null }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Questions["flag"] = new DecisionScoreQuestion
        {
            Instructions = "Score.", Criteria = [JsonSerializer.SerializeToElement(new { description = "no label" })]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Questions = [];
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Model = "digitalocean/";
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request = BooleanRequest();
        request.ProviderOptions = new() { ["digitalocean"] = JsonSerializer.SerializeToElement("invalid") };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData("[{\"type\":\"text\",\"text\":\"context\"},{\"type\":\"file\",\"mediaType\":\"image/png\",\"data\":{\"type\":\"data\",\"data\":\"aW1hZ2U=\"}}]")]
    [InlineData("[{\"type\":\"file\",\"mediaType\":\"audio/wav\",\"data\":{\"type\":\"data\",\"data\":\"YXVkaW8=\"}}]")]
    [InlineData("[{\"type\":\"file\",\"mediaType\":\"video/mp4\",\"data\":{\"type\":\"data\",\"data\":\"dmlkZW8=\"}}]")]
    public async Task Sdk_multimodal_input_is_rejected(string state)
    {
        var request = BooleanRequest();
        request.StateFormat = "sdk";
        request.State = JsonSerializer.Deserialize<JsonElement>(state);
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Native_nontext_arrays_and_structured_instructions_are_rejected()
    {
        var request = BooleanRequest();
        request.State = JsonSerializer.Deserialize<JsonElement>("[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,aW1hZ2U=\"}]");
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().DecisionRequestAsync(request));
        request = BooleanRequest();
        request.Questions["flag"].Instructions = JsonSerializer.SerializeToElement(new { prompt = "Follow up?" });
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(400, "state plus longest question exceeds 32K tokens")]
    [InlineData(413, "request exceeds 64K tokens")]
    [InlineData(403, "this model is not available for your subscription tier")]
    [InlineData(401, "invalid model access key")]
    [InlineData(429, "rate limited")]
    [InlineData(500, "internal error")]
    public async Task Upstream_errors_preserve_status_and_body(int status, string body)
    {
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body)
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(BooleanRequest()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains(body, error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"model":"jev","answers":{},"usage":{"input_tokens":1,"output_tokens":0}}""")]
    [InlineData("""{"model":"jev","answers":{"flag":{"type":"noul","noul":1.1}},"usage":{"input_tokens":1,"output_tokens":0}}""")]
    [InlineData("""{"model":"jev","answers":{"flag":{"type":"noul","noul":null}},"usage":{"input_tokens":1,"output_tokens":0}}""")]
    [InlineData("""{"model":"jev","answers":{"flag":{"type":"predicate","probability":0.4}},"usage":{"input_tokens":1,"output_tokens":0}}""")]
    [InlineData("""{"model":"jev","answers":{"flag":{"type":"noul","noul":0.4},"extra":{"type":"noul","noul":0.2}},"usage":{"input_tokens":1,"output_tokens":0}}""")]
    [InlineData("""{"model":"jev","answers":{"flag":{"type":"noul","noul":0.4},"flag":{"type":"noul","noul":0.2}},"usage":{"input_tokens":1,"output_tokens":0}}""")]
    [InlineData("""{"model":"jev","answers":{"flag":{"type":"noul","noul":0.4}},"usage":{"input_tokens":-1,"output_tokens":0}}""")]
    public async Task Invalid_native_responses_are_reported(string body)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create((_, _) => Task.FromResult(Response(body))).DecisionRequestAsync(BooleanRequest()));
        Assert.Contains("invalid decision response", error.Message);
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("confidence")]
    [InlineData("distribution")]
    [InlineData("duplicate")]
    [InlineData("score")]
    [InlineData("legend")]
    public async Task Invalid_choice_and_score_results_are_not_silently_normalized(string invalid)
    {
        var body = JsonNode.Parse(MixedResponse)!.AsObject();
        var answers = body["answers"]!.AsObject();
        switch (invalid)
        {
            case "choice": answers["sentiment"]!["choice"] = "positive"; break;
            case "confidence": answers["sentiment"]!["confidence"] = -0.1; break;
            case "distribution": answers["sentiment"]!["probabilities"]!["mixed"] = 0.5; break;
            case "duplicate": answers["satisfaction"]!["probabilities"] = JsonNode.Parse("""{"0":0,"1":0.68,"3":0.32}"""); break;
            case "score": answers["satisfaction"]!["score"] = 1.5; break;
            case "legend": answers["satisfaction"]!["legend"] = new JsonObject(); break;
        }
        var request = BooleanRequest();
        request.Questions = new()
        {
            ["followup"] = request.Questions["flag"],
            ["sentiment"] = new DecisionChoiceQuestion
            {
                Instructions = "Choose.", Criteria = new() { ["positive"] = "happy", ["negative"] = "unhappy", ["mixed"] = "both" }
            },
            ["satisfaction"] = new DecisionScoreQuestion { Instructions = "Rate.", Criteria = ["low", "medium", "high"] }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create((_, _) => Task.FromResult(Response(body.ToJsonString()))).DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Cancellation_before_and_during_http_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHttp().DecisionRequestAsync(BooleanRequest(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHttp().OpenAIDecisionRequestAsync(new(), cancelled.Token));

        using var during = new CancellationTokenSource();
        var provider = Create(async (_, ct) =>
        {
            during.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Response(BooleanResponse);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DecisionRequestAsync(BooleanRequest(), during.Token));
    }

    [Fact]
    public async Task Existing_model_discovery_preserves_jev_id_and_authentication()
    {
        var provider = Create((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("/v1/models", http.RequestUri!.AbsolutePath);
            Assert.Equal("digitalocean-key", http.Headers.Authorization!.Parameter);
            return Task.FromResult(Response($$"""{"data":[{"id":"{{Model}}","owned_by":"typesafe"}]}"""));
        });
        var model = Assert.Single(await provider.ListModels());
        Assert.Equal($"digitalocean/{Model}", model.Id);
        Assert.Equal("typesafe", model.OwnedBy);
    }

    [Fact]
    public async Task Missing_credentials_use_existing_authentication_error()
    {
        var provider = Create((_, _) => throw new InvalidOperationException("HTTP must not be sent."), "");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(BooleanRequest()));
        Assert.Contains("API key", error.Message);
    }

    [Fact]
    public async Task Openai_adapter_preserves_typed_choices_names_order_confidence_and_score_scale()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal("/v1/systemone", http.RequestUri!.AbsolutePath);
            Assert.Equal("digitalocean-key", http.Headers.Authorization!.Parameter);
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("feedback", body.GetProperty("state").GetString());
            Assert.Equal(Model, body.GetProperty("model").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("question-0").GetProperty("type").GetString());
            var choices = questions.GetProperty("question-1").GetProperty("criteria");
            Assert.Equal("true: boolean", choices.GetProperty("option-0").GetString());
            Assert.Equal("\"true\": string", choices.GetProperty("option-1").GetString());
            Assert.Equal("middle: average", questions.GetProperty("question-2").GetProperty("criteria")[1].GetString());
            return Response("""
                {"model":"typesafe-jev-1.13.0","answers":{
                  "question-2":{"type":"score","score":1.32,"confidence":0.52,
                    "legend":{"0":"low","1":"middle","2":"high"},"probabilities":{"0":0,"1":0.68,"2":0.32}},
                  "question-1":{"type":"choice","choice":"option-1","confidence":0.8,
                    "probabilities":{"option-0":0.2,"option-1":0.8}},
                  "question-0":{"type":"noul","noul":0.68}},
                 "usage":{"input_tokens":100,"output_tokens":20}}
                """);
        });
        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = $"digitalocean/{Model}", Input = "feedback",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Instructions = "Follow up?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "duplicate", Instructions = "Choose.",
                    Choices =
                    [
                        new() { Value = true, Description = "boolean" },
                        new() { Value = "true", Description = "string" }
                    ]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "duplicate", Instructions = "Rate.",
                    Levels = [new() { Label = "low" }, new() { Label = "middle", Description = "average" }, new() { Label = "high" }]
                }
            ]
        });
        Assert.Equal(Model, response.Model);
        Assert.Null(response.Answers[0].Name);
        Assert.Equal(0.68, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]);
        Assert.Equal("duplicate", choice.Name);
        Assert.Equal(JsonValueKind.String, choice.Choice.Value.ValueKind);
        Assert.Equal("true", choice.Choice.Value.GetString());
        Assert.Equal(0.8, choice.Confidence);
        Assert.Equal(JsonValueKind.True, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[1].Value.Value.ValueKind);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]);
        Assert.Equal("duplicate", score.Name);
        Assert.Equal(1.32, score.Score, 6);
        Assert.Equal(0.52, score.Confidence);
        Assert.Equal(new[] { "low", "middle", "high" }, score.Probabilities.Select(item => item.Label));
        Assert.Equal(new[] { 0d, 1d, 2d }, score.Probabilities.Select(item => item.Value));
        Assert.Equal(100, response.Usage.InputTokens);
        Assert.Equal(20, response.Usage.OutputTokens);
        Assert.Equal(120, response.Usage.TotalTokens);
        Assert.True(response.AdditionalProperties!.ContainsKey("providerMetadata"));
        Assert.Contains("default to zero", response.AdditionalProperties["warnings"][0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Openai_messages_are_preserved_inside_structured_state()
    {
        var input = new OpenAIDecisionInput(new[]
        {
            new OpenAIDecisionInputMessage { Type = "message", Content = "first" },
            new OpenAIDecisionInputMessage
            {
                Content = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputText { Text = "second" },
                    new OpenAIDecisionInputText { Text = "third" }
                }
            }
        });
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(input),
                document.RootElement.GetProperty("state").GetProperty("messages")));
            return Response("""
                {"model":"typesafe-jev-1.13.0","answers":{"question-0":{"type":"noul","noul":0.68}},
                 "usage":{"input_tokens":1,"output_tokens":0}}
                """);
        });
        await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = Model, Input = input,
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Follow up?" }]
        });
    }

    [Fact]
    public async Task Openai_images_and_additional_fields_are_rejected_before_http()
    {
        var request = new OpenAIDecisionRequest
        {
            Model = Model, Input = "feedback",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Follow up?" }],
            AdditionalProperties = new() { ["stream"] = JsonSerializer.SerializeToElement(false) }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().OpenAIDecisionRequestAsync(request));
        request.AdditionalProperties = null;
        request.Input = new OpenAIDecisionInputMessage[]
        {
            new()
            {
                Content = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,aW1hZ2U=" }
                }
            }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().OpenAIDecisionRequestAsync(request));
    }

    [Fact]
    public async Task Openai_single_score_level_does_not_divide_by_zero()
    {
        var provider = Create((_, _) => Task.FromResult(Response("""
            {"model":"typesafe-jev-1.13.0","answers":{
              "question-0":{"type":"score","score":0,"confidence":1,
                "legend":{"0":"only"},"probabilities":{"0":1}}},
             "usage":{"input_tokens":1,"output_tokens":0}}
            """)));
        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = Model, Input = "feedback",
            Questions = [new OpenAIDecisionScoreQuestion { Instructions = "Rate.", Levels = [new() { Label = "only" }] }]
        });
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(Assert.Single(response.Answers));
        Assert.Equal(0, score.Score);
        Assert.Equal(0, Assert.Single(score.Probabilities).Value);
    }

    private static DecisionRequest BooleanRequest() => new()
    {
        Model = $"digitalocean/{Model}", State = "feedback",
        Questions = new() { ["flag"] = new DecisionBooleanQuestion { Instructions = "Follow up?" } }
    };

    private static DigitalOceanProvider NoHttp()
        => Create((_, _) => throw new InvalidOperationException("HTTP must not be sent."));

    private static HttpResponseMessage Response(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private static DigitalOceanProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "digitalocean-key")
        => new(new KeyResolver(key), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
