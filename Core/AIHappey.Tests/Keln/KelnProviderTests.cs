using AIHappey.ChatCompletions.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Keln;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using AIHappey.HeaderAuth;

namespace AIHappey.Tests.Keln;

public sealed class KelnProviderTests
{
    private const string ChatResult = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"native/model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}
        """;
    private const string ResponseResult = """
        {"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"native/model",
         "output":[],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}
        """;
    private const string DecisionResult = """
        {"model":"native/decision","answers":{
         "positive":{"type":"noul","noul":0.9},
         "route":{"type":"choice","choice":"billing","probabilities":{"billing":0.8,"support":0.2}},
         "severity":{"type":"score","score":0.75,"probabilities":{"0":0.25,"1":0.75}}},
         "usage":{"input_tokens":8,"output_tokens":2}}
        """;

    [Fact]
    public void Registration_resolves_the_canonical_scoped_provider()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new KeyResolver());
        services.AddSingleton<IHttpClientFactory>(new Factory((_, _) => throw new InvalidOperationException("Unexpected HTTP.")));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("keln"));
        var provider = Assert.IsType<KelnProvider>(registry.GetModelProvider(scope.ServiceProvider, "keln"));
        Assert.Same(provider, scope.ServiceProvider.GetRequiredKeyedService<IModelProvider>("keln"));
    }

    [Fact]
    public void Header_auth_resolves_the_keln_key()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Keln-Key"] = "keln-header-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("keln-header-key", resolver.Resolve("keln"));
    }

    [Fact]
    public async Task Chat_forwards_all_documented_native_sampling_options_and_image_tool_messages()
    {
        const string options = """
            {"stop":["END"],"seed":42,"n":2,"logprobs":true,"top_logprobs":3,
             "logit_bias":{"12":-1},"frequency_penalty":0.2,"presence_penalty":0.3,
             "parallel_tool_calls":false,"top_k":10,"min_p":0.1,"repetition_penalty":1.1,
             "top_a":0.2,"max_completion_tokens":64,"top_p":0.8,
             "tool_choice":{"type":"function","function":{"name":"lookup"}}}
            """;
        var provider = Create(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            using var expected = JsonDocument.Parse(options);
            foreach (var property in expected.RootElement.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, json.RootElement.GetProperty(property.Name)), property.Name);
            Assert.Equal("image_url", json.RootElement.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("tool", json.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());
            return Json(ChatResult);
        });
        await provider.CompleteChatAsync(new()
        {
            Model = "native/model",
            Messages = [
                new() { Role = "user", Content = Element(new object[] { new { type = "text", text = "Describe" }, new { type = "image_url", image_url = new { url = "data:image/png;base64,AAAA" } } }) },
                new() { Role = "tool", ToolCallId = "call-1", Content = Element("result") }
            ],
            AdditionalProperties = new() { ["providerMetadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["keln"] = JsonDocument.Parse(options).RootElement.Clone() }, JsonSerializerOptions.Web) }
        });
    }

    [Fact]
    public async Task Messages_provider_options_preserve_hosted_search_tool_results_and_thinking()
    {
        var message = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"native/model","max_tokens":128,
             "tools":[{"type":"web_search_20250305","name":"web_search","max_uses":2}],
             "messages":[
              {"role":"assistant","content":[{"type":"tool_use","id":"call-1","name":"lookup","input":{"key":"value"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":"found"}]}],
             "metadata":{"keln":{"service_tier":"lite","temperature":0.2,"thinking":{"type":"enabled","budget_tokens":64}}}}
            """)!;
        message.Headers = new() { ["X-Trace"] = "trace", ["X-Other-Key"] = "secret" };
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("trace", Assert.Single(request.Headers.GetValues("X-Trace")));
            Assert.False(request.Headers.Contains("X-Other-Key"));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert.Equal("lite", body.GetProperty("service_tier").GetString());
            Assert.Equal(0.2, body.GetProperty("temperature").GetDouble(), 5);
            Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("web_search_20250305", body.GetProperty("tools")[0].GetProperty("type").GetString());
            Assert.Equal("tool_result", body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("type").GetString());
            return Json("""{"id":"msg-1","type":"message","role":"assistant","model":"native/model","content":[],"usage":{"input_tokens":2,"output_tokens":1}}""");
        });
        await provider.MessagesAsync(message, []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responses_route_controls_stream_mode_and_auth_not_provider_options(bool streaming)
    {
        var provider = Create(async (request, ct) =>
        {
            Bearer(request);
            Assert.False(request.Headers.Contains("X-Other-Key"));
            Assert.False(request.Headers.Contains("x-api-key"));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(streaming, json.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("native/model", json.RootElement.GetProperty("model").GetString());
            return streaming
                ? new(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") }
                : Json(ResponseResult);
        });
        var options = new ResponseRequest
        {
            Model = "native/model", Input = "hello",
            Headers = new() { ["Authorization"] = "wrong", ["X-Other-Key"] = "secret", ["x-api-key"] = "wrong" },
            Metadata = new() { ["keln"] = Element(new { model = "wrong", stream = !streaming, headers = new Dictionary<string, string> { ["Authorization"] = "wrong", ["X-Other-Key"] = "secret", ["x-api-key"] = "wrong" } }) }
        };
        if (streaming)
            await foreach (var _ in provider.ResponsesStreamingAsync(options)) { }
        else
            await provider.ResponsesAsync(options);
    }

    [Fact]
    public async Task Discovery_preserves_native_ids_and_uses_modalities_not_model_names_for_decisions()
    {
        var calls = 0;
        var provider = Create((request, _) =>
        {
            calls++;
            Assert.Equal("https://api.keln.ai/v1/models", request.RequestUri!.ToString());
            Bearer(request);
            return Task.FromResult(Json("""
                {"data":[
                 {"id":"native/decision","name":"Decision","owned_by":"Keln","context_length":4096,
                  "max_output_tokens":400,"input_modalities":["text","image"],"output_modalities":["decisions"]},
                 {"id":"decision-looking-chat","output_modalities":["text"]},
                 {"id":"mixed","output_modalities":["text","decisions"]}]}
                """));
        });
        var models = (await provider.ListModels()).ToDictionary(m => m.Id);
        Assert.Equal("decision", models["keln/native/decision"].Type);
        Assert.Equal(4096, models["keln/native/decision"].ContextWindow);
        Assert.Equal(400, models["keln/native/decision"].MaxTokens);
        Assert.Contains("image", models["keln/native/decision"].Tags!);
        Assert.Equal("language", models["keln/decision-looking-chat"].Type);
        Assert.Equal("language", models["keln/mixed"].Type);
        await provider.ListModels();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Chat_uses_native_endpoint_options_tools_structured_output_and_usage()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("https://api.keln.ai/v1/chat/completions", request.RequestUri!.ToString());
            Bearer(request);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert.Equal("native/model", body.GetProperty("model").GetString());
            Assert.Equal(8, body.GetProperty("n").GetInt32());
            Assert.Equal(12, body.GetProperty("top_k").GetInt32());
            Assert.Equal("lite", body.GetProperty("service_tier").GetString());
            Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal("function", body.GetProperty("tools")[0].GetProperty("type").GetString());
            return Json(ChatResult);
        });
        var result = await provider.CompleteChatAsync(new()
        {
            Model = "keln/native/model",
            Messages = [new() { Role = "user", Content = Element("hello") }],
            Tools = [JsonSerializer.SerializeToElement(new { type = "function", function = new { name = "lookup", parameters = new { type = "object" } } }, JsonSerializerOptions.Web).ReadChatContract<ChatCompletionToolDefinition>()],
            ResponseFormat = JsonSerializer.SerializeToElement(new { type = "json_object" }, JsonSerializerOptions.Web).ReadChatContract<ChatCompletionResponseFormat>(),
            AdditionalProperties = new() { ["n"] = Element(8) ,["providerMetadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["keln"] = Element(new { top_k = 12, service_tier = "lite" }) }, JsonSerializerOptions.Web)},
        });
        Assert.Equal("chat-1", result.Id);
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public async Task Responses_is_native_and_preserves_web_search_reasoning_and_format()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("https://api.keln.ai/v1/responses", request.RequestUri!.ToString());
            Bearer(request);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert.Equal("native/model", body.GetProperty("model").GetString());
            Assert.Equal("web_search", body.GetProperty("tools")[0].GetProperty("type").GetString());
            Assert.Equal("low", body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal(128, body.GetProperty("max_output_tokens").GetInt32());
            Assert.False(body.GetProperty("store").GetBoolean());
            return Json(ResponseResult);
        });
        var result = await provider.ResponsesAsync(new()
        {
            Model = "keln/native/model", Input = "hello", MaxOutputTokens = 128,
            Reasoning = new() { Effort = "low" },
            Tools = JsonSerializer.Deserialize<List<ResponseToolDefinition>>("""[{"type":"web_search"}]"""),
            Text = new { format = new { type = "json_object" } }, ServiceTier = "standard"
        });
        Assert.Equal("resp-1", result.Id);
        Assert.Equal("keln/native/model", result.Model);
    }

    [Fact]
    public async Task Messages_uses_separate_host_key_and_version_and_native_thinking_and_tools()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("https://anthropic.keln.ai/v1/messages", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("kelntest", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("native/model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("enabled", json.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            return Json("""
                {"id":"msg-1","type":"message","role":"assistant","model":"native/model","content":[{"type":"text","text":"ok"}],
                 "stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}
                """);
        });
        var result = await provider.MessagesAsync(new()
        {
            Model = "keln/native/model", MaxTokens = 128,
            Messages = [],
            Thinking = new() { Type = "enabled", BudgetTokens = 64 },
            ServiceTier = "lite"
        }, new() { ["x-api-key"] = "wrong", ["authorization"] = "wrong", ["anthropic-version"] = "wrong" });
        Assert.Equal("msg-1", result.Id);
        Assert.Equal("keln/native/model", result.Model);
    }

    [Theory]
    [InlineData("standard_only", "standard", false)]
    [InlineData("standard_only", "standard", true)]
    [InlineData("standard", "standard", false)]
    [InlineData("standard", "standard", true)]
    [InlineData("lite", "lite", false)]
    [InlineData("lite", "lite", true)]
    public async Task Messages_normalizes_and_preserves_supported_service_tiers(string tier, string expected, bool providerOptions)
    {
        var provider = Create(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(expected, json.RootElement.GetProperty("service_tier").GetString());
            return Json("""{"id":"msg-1","type":"message","role":"assistant","model":"native/model","content":[],"usage":{"input_tokens":2,"output_tokens":1}}""");
        });
        await provider.MessagesAsync(new()
        {
            Model = "native/model",
            MaxTokens = 128,
            Messages = [],
            ServiceTier = providerOptions ? null : tier,
            Metadata = providerOptions ? new()
            {
                AdditionalProperties = new() { ["keln"] = Element(new { service_tier = tier }) }
            } : null
        }, []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Messages_rejects_unsupported_service_tiers_before_http(bool providerOptions)
    {
        var provider = Create((_, _) => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.MessagesAsync(new()
        {
            Model = "native/model",
            MaxTokens = 128,
            Messages = [],
            ServiceTier = providerOptions ? null : "priority",
            Metadata = providerOptions ? new()
            {
                AdditionalProperties = new() { ["keln"] = Element(new { service_tier = "priority" }) }
            } : null
        }, []));
    }

    [Fact]
    public async Task Decisions_use_system_one_shape_on_decisions_route_and_preserve_usage_and_metadata()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("https://api.keln.ai/v1/decisions", request.RequestUri!.ToString());
            Bearer(request);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.False(json.RootElement.TryGetProperty("input", out _));
            Assert.Equal("evidence", json.RootElement.GetProperty("state").GetString());
            Assert.Equal("noul", json.RootElement.GetProperty("questions").GetProperty("positive").GetProperty("type").GetString());
            Assert.Equal("Billing", json.RootElement.GetProperty("questions").GetProperty("route").GetProperty("criteria").GetProperty("billing").GetString());
            return Json(DecisionResult);
        });
        var result = await provider.DecisionRequestAsync(Decisions());
        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(result.Answers["positive"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score);
        Assert.Equal(8, result.Usage!.InputTokens);
        Assert.Equal("keln/native/decision", result.Response!.ModelId);
        Assert.NotNull(result.ProviderMetadata);
    }

    [Fact]
    public async Task Decisions_forward_noul_criteria_and_accept_the_pinned_model_in_the_response()
    {
        var provider = Create(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("questions").GetProperty("positive").GetProperty("criteria").ValueKind);
            return Json(DecisionResult.Replace("\"model\":\"native/decision\"", "\"model\":\"native/pinned-decision\""));
        });
        var request = Decisions();
        request.Model = "keln/native/decision-alias";
        request.Questions["positive"] = new DecisionBooleanQuestion
        {
            Instructions = "Positive?",
            Criteria = new() { True = "Yes", False = "No" }
        };
        var result = await provider.DecisionRequestAsync(request);
        Assert.Equal("keln/native/pinned-decision", result.Response!.ModelId);
    }

    [Fact]
    public async Task OpenAI_decisions_are_translated_not_sent_as_openai_decisions()
    {
        var provider = Create(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("questions").ValueKind);
            Assert.Equal("noul", json.RootElement.GetProperty("questions").GetProperty("0").GetProperty("type").GetString());
            return Json("""
                {"model":"native/decision","answers":{"0":{"type":"noul","noul":0.9},
                 "1":{"type":"choice","choice":"1","probabilities":{"0":0.2,"1":0.8}},
                 "2":{"type":"score","score":0.75,"probabilities":{"0":0.25,"1":0.75}}},
                 "usage":{"input_tokens":8,"output_tokens":2}}
                """);
        });
        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "native/decision", Input = "evidence",
            Questions = [
                new OpenAIDecisionPredicateQuestion { Name = "p", Instructions = "Positive?" },
                new OpenAIDecisionChoiceQuestion { Name = null, Instructions = "Choose", Choices = [new() { Value = "true" }, new() { Value = true }] },
                new OpenAIDecisionScoreQuestion { Name = "s", Instructions = "Rate", Levels = [new() { Label = "low" }, new() { Label = "high" }] }
            ]
        });
        Assert.Equal("p", result.Answers[0].Name);
        Assert.Null(result.Answers[1].Name);
        Assert.True(Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]).Choice.Value.GetBoolean());
        Assert.Equal("high", Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]).Probabilities[1].Label);
        Assert.Equal(8, result.Usage.InputTokens);
        Assert.Equal(10, result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("""{"store":true}""")]
    [InlineData("""{"previous_response_id":"resp-1"}""")]
    [InlineData("""{"input":[{"type":"item_reference","id":"item-1"}]}""")]
    [InlineData("""{"background":true}""")]
    public async Task Stateless_restrictions_apply_to_native_fields_and_provider_options(string options)
    {
        var provider = NeverSend();
        var direct = JsonSerializer.Deserialize<ResponseRequest>(options)!;
        direct.Model = "native/model";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(direct));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new()
        {
            Model = "native/model", Input = "hello", Metadata = new() { ["keln"] = JsonDocument.Parse(options).RootElement.Clone() }
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Chat_enforces_n_limit_in_both_option_sources(int n)
    {
        var provider = NeverSend();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CompleteChatAsync(new()
        {
            Model = "native/model", AdditionalProperties = new() { ["n"] = Element(n) }
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CompleteChatAsync(new()
        {
            Model = "native/model", AdditionalProperties = new() { ["providerMetadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["keln"] = Element(new { n }) }, JsonSerializerOptions.Web) }
        }));
    }

    [Fact]
    public async Task Unsupported_decision_options_limits_and_question_types_fail_before_http()
    {
        var provider = NeverSend();
        var request = Decisions();
        request.MaxOutputTokens = 4;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request = Decisions();
        request.Questions = Enumerable.Range(0, 401).ToDictionary(i => i.ToString(),
            _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "Yes?" });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.DecisionRequestAsync(request));
        request = Decisions();
        request.Questions["box"] = new DecisionBoxQuestion { Instructions = "Locate" };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
        request = Decisions();
        request.ProviderOptions = new() { ["keln"] = Element(new { stream = true }) };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("lite")]
    public async Task Valid_service_tiers_reach_chat(string tier)
    {
        var provider = Create((_, _) => Task.FromResult(Json(ChatResult)));
        await provider.CompleteChatAsync(new() { Model = "native/model", AdditionalProperties = new() { ["service_tier"] = Element(tier) } });
        await Assert.ThrowsAsync<ArgumentException>(() => NeverSend().ResponsesAsync(new() { Model = "native/model", ServiceTier = "priority" }));
    }

    [Fact]
    public async Task Http_errors_and_cancellation_are_preserved()
    {
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"quota exceeded"}}""")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(Decisions()));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("quota exceeded", error.Message);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NeverSend().DecisionRequestAsync(Decisions(), cts.Token));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_streams_use_correct_routes_and_stream_flag(string protocol)
    {
        var provider = Create(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
            var (url, sse) = protocol switch
            {
                "chat" => ("https://api.keln.ai/v1/chat/completions", """
                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"native/model","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":null}]}

                    data: [DONE]

                    """),
                "responses" => ("https://api.keln.ai/v1/responses", """
                    data: {"type":"response.output_text.delta","item_id":"item-1","output_index":0,"content_index":0,"delta":"ok"}

                    data: [DONE]

                    """),
                _ => ("https://anthropic.keln.ai/v1/messages", """
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

                    data: {"type":"message_stop"}

                    """)
            };
            Assert.Equal(url, request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        });
        var count = 0;
        if (protocol == "chat")
            await foreach (var _ in provider.CompleteChatStreamingAsync(new() { Model = "native/model" })) count++;
        else if (protocol == "responses")
            await foreach (var _ in provider.ResponsesStreamingAsync(new() { Model = "native/model", Input = "hello" })) count++;
        else
            await foreach (var _ in provider.MessagesStreamingAsync(new() { Model = "native/model", MaxTokens = 128, Messages = [] }, [])) count++;
        Assert.True(count > 0);
    }

    [Fact]
    public async Task Malformed_decision_distributions_are_rejected()
    {
        var provider = Create((_, _) => Task.FromResult(Json(DecisionResult.Replace("\"billing\":0.8", "\"billing\":0.5"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(Decisions()));
    }

    private static DecisionRequest Decisions() => new()
    {
        Model = "keln/native/decision", State = "evidence",
        Questions = new()
        {
            ["positive"] = new DecisionBooleanQuestion { Instructions = "Positive?" },
            ["route"] = new DecisionChoiceQuestion { Instructions = "Route", Criteria = new() { ["billing"] = "Billing", ["support"] = "Support" } },
            ["severity"] = new DecisionScoreQuestion { Instructions = "Severity", Criteria = ["low", "high"] }
        }
    };

    private static void Bearer(HttpRequestMessage request)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("kelntest", request.Headers.Authorization?.Parameter);
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static KelnProvider NeverSend() => Create((_, _) => throw new InvalidOperationException("Unexpected HTTP request."));
    private static KelnProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new Factory(responder), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => provider == "keln" ? "kelntest" : null; }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
