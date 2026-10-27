using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.RampRouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Decisions;

public sealed class RampRouterDecisionProviderTests
{
    [Theory]
    [InlineData("gpt-6-luna", "gpt-6-luna")]
    [InlineData("jev-latest", "jev-1.13.0")]
    public async Task Native_contract_uses_shared_auth_and_preserves_questions_and_actual_model(string model, string actualModel)
    {
        var request = NativeRequest(model);
        request.Questions =
        [
            new OpenAIDecisionPredicateQuestion { Name = "urgent", Instructions = "Is it urgent?" },
            new OpenAIDecisionChoiceQuestion
            {
                Name = "match", Instructions = "Pick the exact value.",
                Choices = [new() { Value = "true", Description = "String" }, new() { Value = true }]
            },
            new OpenAIDecisionScoreQuestion
            {
                Name = "severity", Instructions = "Rate severity.",
                Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high", Description = "Severe" }]
            }
        ];
        request.AdditionalProperties = new()
        {
            ["metadata"] = JsonSerializer.SerializeToElement(new { feature = "triage" }),
            ["service_tier"] = JsonSerializer.SerializeToElement("default"),
            ["stream"] = JsonSerializer.SerializeToElement(false)
        };
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal("https://api.router.com/v1/decisions", http.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal(model, body.GetProperty("model").GetString());
            Assert.Equal("triage", body.GetProperty("metadata").GetProperty("feature").GetString());
            Assert.Equal("default", body.GetProperty("service_tier").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.Equal(JsonSerializer.SerializeToElement(request.Questions).GetRawText(), body.GetProperty("questions").GetRawText());
            return Response("""
                [{"type":"predicate","name":"urgent","probability":0.92},
                 {"type":"choice","name":"match","choice":true,"probabilities":[
                    {"value":"true","probability":0.1},{"value":true,"probability":0.9}],"confidence":0.9},
                 {"type":"score","name":"severity","score":1.25,"probabilities":[
                    {"value":0,"label":"low","probability":0.25},
                    {"value":1,"label":"medium","probability":0.25},
                    {"value":2,"label":"high","probability":0.5}]}]
                """, actualModel);
        });

        request.Model = "ramprouter/" + model;
        var response = await provider.OpenAIDecisionRequestAsync(request);
        Assert.Equal("ramprouter/" + model, request.Model);
        Assert.Equal(actualModel, response.Model);
        Assert.Equal(0.92, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        Assert.Equal(JsonValueKind.True, Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]).Choice.Value.ValueKind);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]);
        Assert.Equal(1.25, score.Score);
        Assert.Equal(new[] { 0d, 1d, 2d }, score.Probabilities.Select(item => item.Value));
        Assert.Equal(0, score.Confidence);
        Assert.Contains("omitted confidence", response.AdditionalProperties!["warnings"].GetRawText());
        Assert.Equal(384, response.Usage.TotalTokens);
        Assert.Equal(0, response.Usage.InputTokensDetails.CachedTokens);
    }

    [Fact]
    public async Task Sdk_contract_maps_all_question_types_and_preserves_raw_response_and_headers()
    {
        var provider = Create(async (http, ct) =>
        {
            Assert.Equal("trace", Assert.Single(http.Headers.GetValues("x-ramprouter-trace")));
            Assert.Equal("test-key", http.Headers.Authorization!.Parameter);
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("{\"ticket\":\"refund\"}", body.GetProperty("input").GetString());
            Assert.Equal("triage", body.GetProperty("metadata").GetProperty("feature").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("predicate", questions[0].GetProperty("type").GetString());
            Assert.True(questions[1].GetProperty("choices")[0].GetProperty("value").GetBoolean());
            Assert.Equal("0", questions[3].GetProperty("levels")[0].GetProperty("label").GetString());
            return Response("""
                [{"type":"predicate","name":"urgent","probability":0.92},
                 {"type":"choice","name":"boolean","choice":false,"confidence":0.8,"probabilities":[
                    {"value":true,"probability":0.2},{"value":false,"probability":0.8}]},
                 {"type":"choice","name":"route","choice":"billing","confidence":0.8,"probabilities":[
                    {"value":"billing","probability":0.8},{"value":"support","probability":0.2}]},
                 {"type":"score","name":"severity","score":1.25,"confidence":0.5,"probabilities":[
                    {"value":0,"label":"0","probability":0.25},
                    {"value":1,"label":"1","probability":0.25},
                    {"value":2,"label":"2","probability":0.5}]}]
                """, "jev-1.13.0");
        });
        var request = SdkRequest();
        request.State = JsonSerializer.SerializeToElement(new { ticket = "refund" });
        request.Headers = new() { ["Authorization"] = "attacker", ["x-ramprouter-trace"] = "trace" };
        request.ProviderOptions = new()
        {
            ["ramprouter"] = JsonSerializer.SerializeToElement(new { metadata = new { feature = "triage" } })
        };
        request.Questions["boolean"] = new DecisionBooleanQuestion
        {
            Instructions = "Refund?", Criteria = new() { True = "Refund requested", False = "Not a refund" }
        };
        request.Questions["route"] = new DecisionChoiceQuestion
        {
            Instructions = "Which team?", Criteria = new() { ["billing"] = "Payments", ["support"] = null }
        };
        request.Questions["severity"] = new DecisionScoreQuestion { Instructions = "Rate.", Criteria = ["low", "medium", "high"] };
        var response = await provider.DecisionRequestAsync(request);
        Assert.Equal(0.92, Assert.IsType<DecisionBooleanAnswer>(response.Answers["urgent"]).Probability);
        Assert.Equal(0.2, Assert.IsType<DecisionBooleanAnswer>(response.Answers["boolean"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]).Choice);
        Assert.Equal(1.25, Assert.IsType<DecisionScoreAnswer>(response.Answers["severity"]).Score);
        Assert.Equal("ramprouter/jev-1.13.0", response.Response!.ModelId);
        Assert.Equal("request-id", response.Response.Headers!["x-request-id"]);
        Assert.False(response.ProviderMetadata!["ramprouter"].GetProperty("usage").TryGetProperty("total_tokens", out _));
        Assert.True(JsonElement.DeepEquals(response.ProviderMetadata["ramprouter"], Assert.IsType<JsonElement>(response.Response.Body)));
        Assert.Equal(382, response.Usage!.InputTokens);
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task Openai_images_and_sdk_image_parts_are_forwarded_without_loss()
    {
        var sends = 0;
        var provider = Create(async (http, ct) =>
        {
            sends++;
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var content = document.RootElement.GetProperty("input")[0].GetProperty("content");
            Assert.Equal("evidence", content[0].GetProperty("text").GetString());
            Assert.Equal("data:image/png;base64,AQ==", content[1].GetProperty("image_url").GetString());
            if (sends == 1)
                Assert.Equal(JsonValueKind.Null, content[1].GetProperty("detail").ValueKind);
            return Response("""[{"type":"predicate","name":"urgent","probability":0.7}]""", "gpt-6-luna");
        });
        var request = SdkRequest();
        request.Model = "gpt-6-luna";
        request.State = JsonSerializer.Deserialize<JsonElement>("""
            [{"role":"user","content":[{"type":"input_text","text":"evidence"},
              {"type":"input_image","image_url":"data:image/png;base64,AQ==","detail":null}]}]
            """);
        await provider.DecisionRequestAsync(request);
        request.StateFormat = "sdk";
        request.State = JsonSerializer.Deserialize<JsonElement>("""
            [{"type":"text","text":"evidence"},
             {"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQ=="}}]
            """);
        await provider.DecisionRequestAsync(request);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task Native_message_input_safety_identifier_and_reported_usage_details_are_preserved()
    {
        var provider = Create(async (http, ct) =>
        {
            using var document = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            Assert.Equal("caller-id", body.GetProperty("safety_identifier").GetString());
            Assert.Equal("high", body.GetProperty("input")[0].GetProperty("content")[0].GetProperty("detail").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"gpt-6-luna","answers":[{"type":"predicate","name":"urgent","probability":0.8}],
                     "usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12,
                       "input_tokens_details":{"cached_tokens":5,"cache_write_tokens":1},
                       "output_tokens_details":{"reasoning_tokens":2}},"future_field":"preserved"}
                    """)
            };
        });
        var request = NativeRequest();
        request.SafetyIdentifier = "caller-id";
        request.Input = new OpenAIDecisionInputMessage[]
        {
            new() { Content = new OpenAIDecisionInputPart[]
            {
                new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==", Detail = "high" }
            } }
        };
        var response = await provider.OpenAIDecisionRequestAsync(request);
        Assert.Equal(12, response.Usage.TotalTokens);
        Assert.Equal(5, response.Usage.InputTokensDetails.CachedTokens);
        Assert.Equal(1, response.Usage.InputTokensDetails.CacheWriteTokens);
        Assert.Equal(2, response.Usage.OutputTokensDetails.ReasoningTokens);
        Assert.Equal("preserved", response.AdditionalProperties!["future_field"].GetString());
        Assert.False(response.AdditionalProperties.ContainsKey("warnings"));
    }

    [Fact]
    public async Task Sdk_missing_confidence_warns_without_altering_raw_evidence()
    {
        var provider = Create((_, _) => Task.FromResult(Response("""
            [{"type":"choice","name":"route","choice":"yes","probabilities":[
                {"value":"yes","probability":0.9},{"value":"no","probability":0.1}]}]
            """, "jev-1.13.0")));
        var request = SdkRequest();
        request.Questions = new()
        {
            ["route"] = new DecisionChoiceQuestion
            {
                Instructions = "Choose.", Criteria = new() { ["yes"] = null, ["no"] = null }
            }
        };
        var response = await provider.DecisionRequestAsync(request);
        Assert.Equal("yes", Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]).Choice);
        Assert.Single(response.Warnings);
        Assert.False(response.ProviderMetadata!["ramprouter"].GetProperty("answers")[0].TryGetProperty("confidence", out _));
    }

    [Theory]
    [InlineData("""{"stream":true}""")]
    [InlineData("""{"stream":"false"}""")]
    [InlineData("""{"allow_flex_tier":false}""")]
    [InlineData("""{"service_tier":"flex"}""")]
    [InlineData("""{"models":["gpt-6-luna"]}""")]
    [InlineData("""{"metadata":"invalid"}""")]
    [InlineData("""{"strategy":"cost-efficient"}""")]
    [InlineData("""{"unknown":true}""")]
    public async Task Unsupported_options_are_rejected_for_both_contracts(string options)
    {
        var provider = NoSend();
        var native = NativeRequest();
        native.AdditionalProperties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(options);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(native));
        var sdk = SdkRequest();
        sdk.ProviderOptions = new() { ["ramprouter"] = JsonSerializer.Deserialize<JsonElement>(options) };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(sdk));
    }

    [Fact]
    public async Task Jev_rejects_missing_or_duplicate_names_and_images_and_all_models_reject_flex_pins()
    {
        var provider = NoSend();
        var request = NativeRequest("jev-latest");
        request.Questions[0].Name = null;
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(request));
        request.Questions = [request.Questions[0], request.Questions[0]];
        request.Questions[0].Name = "duplicate";
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(request));
        request = NativeRequest("jev-latest");
        request.Input = new OpenAIDecisionInputMessage[]
        {
            new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" } } }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(request));
        request = NativeRequest("openai:gpt-6-luna:flex");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(request));
        request = NativeRequest("jev-latest");
        request.Questions = [new OpenAIDecisionChoiceQuestion
        {
            Name = "duplicate-values", Instructions = "Pick.",
            Choices = [new() { Value = "duplicate" }, new() { Value = "duplicate" }]
        }];
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIDecisionRequestAsync(request));
        var sdk = SdkRequest();
        sdk.StateFormat = "sdk";
        sdk.State = JsonSerializer.Deserialize<JsonElement>("""
            [{"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQ=="}}]
            """);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(sdk));
    }

    [Fact]
    public async Task Sdk_unsupported_contract_features_and_canonical_overrides_are_rejected()
    {
        var provider = NoSend();
        var request = SdkRequest();
        request.MaxOutputTokens = 10;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.MaxOutputTokens = null;
        request.Reasoning = JsonSerializer.SerializeToElement(new { });
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.Reasoning = null;
        request.Questions["box"] = new DecisionBoxQuestion { Instructions = "Locate it." };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request.Questions.Remove("box");
        request.ProviderOptions = new() { ["ramprouter"] = JsonSerializer.SerializeToElement(new { model = "override" }) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(501)]
    [InlineData(529)]
    public async Task Provider_errors_use_shared_error_handling(int status)
    {
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("""{"error":{"message":"provider failure"}}""")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIDecisionRequestAsync(NativeRequest()));
        Assert.Contains(status.ToString(), error.Message);
        Assert.Contains("provider failure", error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"model":"gpt-6-luna","answers":[],"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"model":"gpt-6-luna","answers":[{"type":"predicate","name":"urgent","probability":2}],"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"model":"gpt-6-luna","answers":[{"type":"predicate","name":"urgent","probability":0.5}],"usage":{"input_tokens":2147483647,"output_tokens":1}}""")]
    public async Task Malformed_responses_are_rejected(string body)
    {
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIDecisionRequestAsync(NativeRequest()));
        Assert.Contains("invalid OpenAI-compatible response", error.Message);
    }

    [Fact]
    public async Task Cancellation_and_missing_credentials_do_not_send_requests()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoSend().OpenAIDecisionRequestAsync(NativeRequest(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoSend().DecisionRequestAsync(SdkRequest(), cancellation.Token));
        var provider = Create((_, _) => throw new InvalidOperationException("Must not send."), key: null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIDecisionRequestAsync(NativeRequest()));
        Assert.Contains("API key", error.Message);
    }

    [Fact]
    public async Task Existing_discovery_includes_decisions_only_models()
    {
        var provider = Create((http, _) =>
        {
            Assert.Equal("/v1/models", http.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", http.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"jev-latest","owned_by":"typesafe","router":{"surfaces":["decisions","systemone"]}}]}""")
            });
        });
        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("ramprouter/jev-latest", model.Id);
    }

    private static OpenAIDecisionRequest NativeRequest(string model = "gpt-6-luna") => new()
    {
        Model = model, Input = "evidence",
        Questions = [new OpenAIDecisionPredicateQuestion { Name = "urgent", Instructions = "Is it urgent?" }]
    };

    private static DecisionRequest SdkRequest() => new()
    {
        Model = "ramprouter/jev-latest", State = "evidence",
        Questions = new() { ["urgent"] = new DecisionBooleanQuestion { Instructions = "Is it urgent?" } }
    };

    private static HttpResponseMessage Response(string answers, string model)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"model\":\"" + model + "\",\"answers\":" + answers + ",\"usage\":{\"input_tokens\":382,\"output_tokens\":2}}",
                Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private static RampRouterProvider NoSend() => Create((_, _) => throw new InvalidOperationException("Request must not be sent."));

    private static RampRouterProvider Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
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

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
