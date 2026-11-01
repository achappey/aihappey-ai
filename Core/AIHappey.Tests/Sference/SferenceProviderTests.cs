using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Sference;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Sference;

public sealed class SferenceProviderTests
{
    [Fact]
    public async Task Discovery_preserves_catalog_ids_and_marks_clef_as_decision()
    {
        var provider = Create((request, _) =>
        {
            Assert.Equal("https://api.sference.com/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse("""
                {"data":[
                  {"id":"Cloudflare/clef","owned_by":"Cloudflare","created":123},
                  {"id":"moonshotai/Kimi-K3","name":"Kimi K3","context_window":262144},
                  {"id":"zai-org/GLM-5.3"}
                ]}
                """));
        });
        var models = (await provider.ListModels()).ToDictionary(model => model.Id);
        Assert.Equal(3, models.Count);
        var clef = models["sference/Cloudflare/clef"];
        Assert.Equal("decision", clef.Type);
        Assert.Equal(65536, clef.ContextWindow);
        Assert.Equal(0.24m, clef.Pricing!.Input);
        Assert.Equal(0m, clef.Pricing.Output);
        Assert.Equal("Cloudflare", clef.OwnedBy);
        Assert.Equal(123, clef.Created);
        Assert.Equal("language", models["sference/moonshotai/Kimi-K3"].Type);
        Assert.Equal(262144, models["sference/moonshotai/Kimi-K3"].ContextWindow);
    }

    [Fact]
    public async Task Native_decisions_preserve_json_state_optional_instructions_and_all_question_types()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = doc.RootElement;
            Assert.Equal("Cloudflare/clef", body.GetProperty("model").GetString());
            Assert.Equal(42, body.GetProperty("state").GetProperty("ticket").GetInt32());
            var questions = body.GetProperty("questions");
            Assert.False(questions.GetProperty("route").TryGetProperty("instructions", out _));
            Assert.Equal("noul", questions.GetProperty("refund").GetProperty("type").GetString());
            Assert.Equal("Requested refund", questions.GetProperty("refund").GetProperty("criteria").GetProperty("true").GetString());
            return JsonResponse("""
                {"model":"Cloudflare/clef","answers":{
                 "route":{"type":"choice","choice":"billing","confidence":0.9909,
                   "probabilities":{"billing":0.9909,"technical":0.0055,"sales":0.0036}},
                 "urgency":{"type":"score","score":1.5588,"confidence":0.5892,
                   "legend":{"0":"Low","1":"Medium","2":"High"},
                   "probabilities":{"0":0.0304,"1":0.3804,"2":0.5892}},
                 "refund":{"type":"noul","noul":0.9928}},
                 "usage":{"input_tokens":323,"output_tokens":0}}
                """);
        });
        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "sference/Cloudflare/clef",
            State = JsonSerializer.SerializeToElement(new { ticket = 42 }),
            Questions = new()
            {
                ["route"] = new DecisionChoiceQuestion
                {
                    Criteria = new() { ["billing"] = null, ["technical"] = "Bugs", ["sales"] = "Plans" }
                },
                ["urgency"] = new DecisionScoreQuestion { Criteria = [new("Low"), new("Medium"), new("High")] },
                ["refund"] = new DecisionBooleanQuestion { Criteria = new() { True = "Requested refund", False = "No refund" } }
            }
        });
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]).Choice);
        Assert.Equal(1.5588, Assert.IsType<DecisionScoreAnswer>(response.Answers["urgency"]).Score);
        Assert.Equal(0.9928, Assert.IsType<DecisionBooleanAnswer>(response.Answers["refund"]).Probability);
        Assert.Equal(323, response.Usage!.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal("sference/Cloudflare/clef", response.Response!.ModelId);
        Assert.NotNull(response.ProviderMetadata);
    }

    [Fact]
    public async Task OpenAI_decisions_restore_typed_choices_names_and_order()
    {
        var provider = Create(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("noul", doc.RootElement.GetProperty("questions").GetProperty("0").GetProperty("type").GetString());
            Assert.Equal(new[] { "0", "1" }, doc.RootElement.GetProperty("questions").GetProperty("1")
                .GetProperty("criteria").EnumerateObject().Select(p => p.Name));
            return JsonResponse("""
                {"model":"Cloudflare/clef","answers":{
                  "2":{"type":"score","score":0.8,"confidence":0.8,"probabilities":{"0":0.2,"1":0.8}},
                  "1":{"type":"choice","choice":"1","confidence":0.7,"probabilities":{"0":0.3,"1":0.7}},
                  "0":{"type":"noul","noul":0.9}},
                 "usage":{"input_tokens":12,"output_tokens":0}}
                """);
        });
        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "sference/Cloudflare/clef", Input = "input",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "valid", Instructions = "Is it valid?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "typed", Instructions = "Which type?",
                    Choices = [new() { Value = true }, new() { Value = "true" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "How much?", Levels = [new() { Label = "low" }, new() { Label = "high" }]
                }
            ]
        });
        Assert.Equal("Cloudflare/clef", response.Model);
        Assert.Equal(new string?[] { "valid", "typed", null }, response.Answers.Select(a => a.Name));
        Assert.Equal(0.9, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]);
        Assert.Equal(JsonValueKind.String, choice.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.True, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal("high", Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]).Probabilities[1].Label);
        Assert.Equal(12, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task Chat_uses_native_tools_vision_structured_output_and_transport_headers()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("chat-1", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = doc.RootElement;
            Assert.Equal("moonshotai/Kimi-K3", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.Equal("image_url", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("function", body.GetProperty("tools")[0].GetProperty("type").GetString());
            Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal(20, body.GetProperty("max_queue_wait_s").GetInt32());
            return JsonResponse("""{"id":"chat1","model":"moonshotai/Kimi-K3","choices":[{"message":{"tool_calls":[{"id":"tool1","type":"function","function":{"name":"lookup","arguments":"{}"}}]}}],"usage":{"prompt_tokens":12,"completion_tokens":4}}""");
        });
        var result = await provider.CompleteChatAsync(new()
        {
            Model = "sference/moonshotai/Kimi-K3", Stream = true,
            Headers = new() { ["Idempotency-Key"] = "chat-1" },
            Messages = [new() { Role = "user", Content = Element("""[{"type":"text","text":"Look"},{"type":"image_url","image_url":{"url":"https://example.com/image.png"}}]""") }],
            Tools = [new { type = "function", function = new { name = "lookup", parameters = new { type = "object" } } }],
            ResponseFormat = new { type = "json_schema", json_schema = new { name = "result", schema = new { type = "object" } } },
            Metadata = new() { ["sference"] = Element("""{"max_queue_wait_s":20,"service_tier":"flex","prompt_cache_key":"prefix"}""") }
        });
        Assert.Equal("sference/moonshotai/Kimi-K3", result.Model);
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public async Task Responses_preserves_background_webhook_stream_metadata_and_native_tools()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = doc.RootElement;
            Assert.True(body.GetProperty("background").GetBoolean());
            Assert.Equal("24h", body.GetProperty("metadata").GetProperty("completion_window").GetString());
            Assert.Equal("stream-1", body.GetProperty("metadata").GetProperty("stream_id").GetString());
            Assert.False(body.GetProperty("metadata").TryGetProperty("sference", out _));
            Assert.Equal("priority", body.GetProperty("service_tier").GetString());
            Assert.Equal("function", body.GetProperty("tools")[0].GetProperty("type").GetString());
            return JsonResponse("""{"id":"resp1","model":"zai-org/GLM-5.3","status":"queued","output":[]}""");
        });
        var response = await provider.ResponsesAsync(new()
        {
            Model = "sference/zai-org/GLM-5.3", Background = true,
            Input = new ResponseInput("hello"),
            Metadata = new()
            {
                ["completion_window"] = "24h", ["stream_id"] = "stream-1",
                ["completion_webhook"] = "https://example.com/callback",
                ["sference"] = Element("""{"service_tier":"priority","max_queue_wait_s":5}""")
            },
            Tools = [new() { Type = "function", Extra = new() { ["name"] = Element("\"lookup\"") } }]
        });
        Assert.Equal("sference/zai-org/GLM-5.3", response.Model);
    }

    [Fact]
    public async Task Responses_passes_multimodal_input_and_structured_output_without_chat_translation()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = doc.RootElement;
            Assert.Equal("input_image", body.GetProperty("input")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("json_schema", body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
            return JsonResponse("""{"id":"r1","model":"moonshotai/Kimi-K3","status":"completed","output":[],"usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12}}""");
        });
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"sference/moonshotai/Kimi-K3",
             "input":[{"type":"message","role":"user","content":[
                 {"type":"input_text","text":"Describe"},{"type":"input_image","image_url":"data:image/png;base64,AQ=="}]}],
             "text":{"format":{"type":"json_schema","name":"caption","schema":{"type":"object"}}},
             "reasoning":{"effort":"high"}}
            """, ResponseJson.Default)!;
        var result = await provider.ResponsesAsync(request);
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public async Task Messages_preserves_multiple_OpenAI_tools_and_nested_tool_result_images()
    {
        var provider = Create(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(2, doc.RootElement.GetProperty("tools").GetArrayLength());
            Assert.Equal("second", doc.RootElement.GetProperty("tools")[1].GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("image", doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0]
                .GetProperty("content")[0].GetProperty("type").GetString());
            return JsonResponse("""{"model":"moonshotai/Kimi-K3","content":[{"type":"text","text":"Done"}]}""");
        });
        var request = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"moonshotai/Kimi-K3","max_tokens":20,"messages":[
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"tool1","content":[
                {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQ=="}}]}]}],
             "tools":[
              {"type":"function","function":{"name":"first","parameters":{"type":"object"}}},
              {"type":"function","function":{"name":"second","parameters":{"type":"object"}}}]}
            """, MessagesJson.Default)!;
        await provider.MessagesAsync(request, []);
    }

    [Fact]
    public async Task Messages_maps_forced_tool_and_preserves_images_and_cached_usage()
    {
        var provider = Create(async (request, ct) =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal("message-1", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = doc.RootElement;
            Assert.Equal("moonshotai/Kimi-K3", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.Equal("function", body.GetProperty("tool_choice").GetProperty("type").GetString());
            Assert.Equal("lookup", body.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("image", body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());
            return JsonResponse("""{"id":"msg1","type":"message","model":"moonshotai/Kimi-K3","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"lookup","input":{}}],"stop_reason":"tool_use","usage":{"input_tokens":12,"output_tokens":4,"cache_read_input_tokens":8}}""");
        });
        var result = await provider.MessagesAsync(new()
        {
            Model = "sference/moonshotai/Kimi-K3", MaxTokens = 100,
            Messages = [new() { Role = "user", Content = new MessagesContent(Element("""[{"type":"image","source":{"type":"url","url":"https://example.com/image.png"}}]""")) }],
            ToolChoice = new() { Type = "tool", AdditionalProperties = new() { ["name"] = Element("\"lookup\"") } },
            Tools = [new() { Name = "lookup", InputSchema = Element("""{"type":"object"}""") }],
            AdditionalProperties = new() { ["max_queue_wait_s"] = Element("10"), ["enable_thinking"] = Element("true") }
        }, new() { ["Idempotency-Key"] = "message-1" });
        Assert.Equal("sference/moonshotai/Kimi-K3", result.Model);
        Assert.Equal("tool_use", result.StopReason);
        Assert.Equal(8, result.Usage!.CacheReadInputTokens);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_streams_preserve_tool_deltas_usage_and_error_events(string surface)
    {
        var provider = Create(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
            var data = surface switch
            {
                "chat" => """
                    data: {"id":"c1","model":"zai-org/GLM-5.3","choices":[{"delta":{"tool_calls":[{"index":0,"id":"t1","function":{"name":"lookup","arguments":"{}"}}]}}]}

                    data: {"id":"c1","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":3}}

                    data: {"error":{"message":"Try later","type":"api_error"}}

                    data: [DONE]

                    """,
                "responses" => """
                    event: response.function_call_arguments.delta
                    data: {"type":"response.function_call_arguments.delta","item_id":"t1","output_index":0,"delta":"{}"}

                    event: error
                    data: {"type":"error","code":"overloaded","message":"Try later"}

                    """,
                _ => """
                    event: message_start
                    data: {"type":"message_start","message":{"id":"m1","model":"zai-org/GLM-5.3","role":"assistant","content":[],"usage":{"input_tokens":10,"output_tokens":0}}}

                    event: content_block_delta
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{}"}}

                    event: message_delta
                    data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":3,"cache_read_input_tokens":8}}

                    event: error
                    data: {"type":"error","error":{"type":"api_error","message":"Try later"}}

                    """
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        });
        if (surface == "chat")
        {
            var parts = new List<ChatCompletionUpdate>();
            await foreach (var part in provider.CompleteChatStreamingAsync(new() { Model = "zai-org/GLM-5.3" })) parts.Add(part);
            Assert.Equal(3, parts.Count);
            Assert.Equal("sference/zai-org/GLM-5.3", parts[0].Model);
            Assert.Contains("tool_calls", JsonSerializer.Serialize(parts[0].Choices));
            Assert.NotNull(parts[1].Usage);
            Assert.Equal("Try later", parts[2].AdditionalProperties!["error"].GetProperty("message").GetString());
        }
        else if (surface == "responses")
        {
            var types = new List<string>();
            await foreach (var part in provider.ResponsesStreamingAsync(new() { Model = "zai-org/GLM-5.3", Input = new ResponseInput("hello") }))
                types.Add(part.Type);
            Assert.Equal(new[] { "response.function_call_arguments.delta", "error" }, types);
        }
        else
        {
            var parts = new List<MessageStreamPart>();
            await foreach (var part in provider.MessagesStreamingAsync(new() { Model = "zai-org/GLM-5.3", MaxTokens = 100, Messages = [] }, [])) parts.Add(part);
            Assert.Equal("sference/zai-org/GLM-5.3", parts[0].Message!.Model);
            Assert.Equal("{}", parts[1].Delta!.PartialJson);
            Assert.Equal(8, parts[2].Usage!.CacheReadInputTokens);
            Assert.Equal("Try later", parts[3].Error!.Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task Decision_question_count_limits_fail_before_network(int count)
    {
        var provider = NoNetwork();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(new()
        {
            Model = "Cloudflare/clef", State = "input",
            Questions = Enumerable.Range(0, count).ToDictionary(i => i.ToString(), _ => (DecisionQuestion)new DecisionBooleanQuestion())
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task Decision_criteria_limits_fail_before_network(int count)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NoNetwork().DecisionRequestAsync(new()
        {
            Model = "Cloudflare/clef", State = "input",
            Questions = new() { ["score"] = new DecisionScoreQuestion { Criteria = Enumerable.Repeat<DecisionInput?>(new("level"), count).ToArray() } }
        }));
    }

    [Fact]
    public async Task Decision_body_limit_counts_utf8_bytes_and_rejects_unsupported_options()
    {
        var request = Decision();
        request.State = new string('\u20ac', 400000);
        await Assert.ThrowsAsync<ArgumentException>(() => NoNetwork().DecisionRequestAsync(request));
        request = Decision();
        request.MaxOutputTokens = 10;
        await Assert.ThrowsAsync<NotSupportedException>(() => NoNetwork().DecisionRequestAsync(request));
        request = Decision();
        request.ProviderOptions = new() { ["sference"] = Element("""{"service_tier":"flex"}""") };
        await Assert.ThrowsAsync<NotSupportedException>(() => NoNetwork().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Decision_body_limit_accepts_exactly_one_MiB_and_rejects_one_more_byte()
    {
        var emptyBody = """{"model":"Cloudflare/clef","state":"","questions":{"q":{"type":"noul"}}}""";
        var request = Decision();
        request.State = new string('a', 1024 * 1024 - Encoding.UTF8.GetByteCount(emptyBody));
        var provider = Create(async (message, ct) =>
        {
            Assert.Equal(1024 * 1024, (await message.Content!.ReadAsByteArrayAsync(ct)).Length);
            return JsonResponse("""{"model":"Cloudflare/clef","answers":{"q":{"type":"noul","noul":0.5}},"usage":{"input_tokens":1,"output_tokens":0}}""");
        });
        await provider.DecisionRequestAsync(request);
        request.State = request.State.Value.GetString()! + "a";
        await Assert.ThrowsAsync<ArgumentException>(() => NoNetwork().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Decision_maximum_question_and_criteria_counts_and_singleton_criteria_are_supported()
    {
        var provider = Create(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var questions = doc.RootElement.GetProperty("questions");
            Assert.Equal(64, questions.EnumerateObject().Count());
            Assert.Equal(256, questions.GetProperty("q0").GetProperty("criteria").EnumerateObject().Count());
            var answers = Enumerable.Range(0, 64).ToDictionary(i => $"q{i}", i => (object)(i switch
            {
                0 => new { type = "choice", choice = "v0", confidence = 1.0,
                    probabilities = Enumerable.Range(0, 256).ToDictionary(j => $"v{j}", j => j == 0 ? 1.0 : 0.0) },
                1 => new { type = "score", score = 0.0, confidence = 1.0, probabilities = new Dictionary<string, double> { ["0"] = 1 } },
                _ => new { type = "noul", noul = 0.5 }
            }));
            return JsonResponse(JsonSerializer.Serialize(new
            {
                model = "Cloudflare/clef", answers, usage = new { input_tokens = 10, output_tokens = 0 }
            }));
        });
        var questions = Enumerable.Range(0, 64).ToDictionary(i => $"q{i}", _ => (DecisionQuestion)new DecisionBooleanQuestion());
        questions["q0"] = new DecisionChoiceQuestion
        {
            Criteria = Enumerable.Range(0, 256).ToDictionary(i => $"v{i}", _ => (DecisionInput?)null)
        };
        questions["q1"] = new DecisionScoreQuestion { Criteria = [new("Only")] };
        var result = await provider.DecisionRequestAsync(new() { Model = "Cloudflare/clef", State = "input", Questions = questions });
        Assert.Equal(64, result.Answers.Count);
    }

    [Fact]
    public async Task SDK_decision_images_are_rejected_instead_of_being_treated_as_JSON_evidence()
    {
        var request = Decision();
        request.StateFormat = "sdk";
        request.State = Element("""[{"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQ=="}}]""");
        await Assert.ThrowsAsync<NotSupportedException>(() => NoNetwork().DecisionRequestAsync(request));
    }

    [Fact]
    public async Task OpenAI_decisions_reject_images_and_safety_identifier()
    {
        var request = new OpenAIDecisionRequest
        {
            Model = "Cloudflare/clef", Input = "input", SafetyIdentifier = "opaque-user",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it valid?" }]
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => NoNetwork().OpenAIDecisionRequestAsync(request));
        request.SafetyIdentifier = null;
        request.Input = new OpenAIDecisionInputMessage[]
        {
            new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" } } }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => NoNetwork().OpenAIDecisionRequestAsync(request));
    }

    [Fact]
    public async Task Missing_credentials_fail_explicitly_before_network()
    {
        var provider = new SferenceProvider(new KeyResolver(null), new ClientFactory(
            new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network request.")))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels());
        Assert.Equal("No Sference API key.", error.Message);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public async Task Queue_bounds_are_enforced_for_all_native_surfaces(int seconds)
    {
        var properties = new Dictionary<string, JsonElement> { ["max_queue_wait_s"] = Element(seconds.ToString()) };
        var provider = NoNetwork();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteChatAsync(new() { Model = "model", AdditionalProperties = properties }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResponsesAsync(new() { Model = "model", AdditionalProperties = properties }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.MessagesAsync(new() { Model = "model", MaxTokens = 10, Messages = [], AdditionalProperties = properties }, []));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    public async Task Messages_rejects_non_positive_token_limits_before_network(int maxTokens, bool stream)
    {
        var provider = NoNetwork();
        var request = new MessagesRequest { Model = "model", MaxTokens = maxTokens, Messages = [] };
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            if (stream)
            {
                await foreach (var _ in provider.MessagesStreamingAsync(request, [])) { }
            }
            else
                await provider.MessagesAsync(request, []);
        });

        Assert.Equal("Sference Messages requires a positive max_tokens.", error.Message);
    }

    [Fact]
    public async Task Unsupported_state_hosted_tools_and_message_options_are_explicit()
    {
        var provider = NoNetwork();
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new()
        {
            Model = "model", AdditionalProperties = new() { ["previous_response_id"] = Element("\"resp_1\"") }
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new()
        {
            Model = "model", Tools = [new() { Type = "web_search" }]
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.MessagesAsync(new()
        {
            Model = "model", MaxTokens = 10, Messages = [], ServiceTier = "flex"
        }, []));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.MessagesAsync(new()
        {
            Model = "model", MaxTokens = 10,
            Messages = [new() { Role = "user", Content = new MessagesContent([new() { Type = "document" }]) }]
        }, []));
    }

    [Fact]
    public async Task Decisions_propagate_cancellation_status_and_retry_after()
    {
        var provider = Create((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"error":{"message":"No decision capacity"}}""")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "5");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(Decision()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Contains("Retry-After: 5", error.Message);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoNetwork().DecisionRequestAsync(Decision(), cts.Token));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_requests_propagate_inflight_cancellation(string surface)
    {
        using var cts = new CancellationTokenSource();
        var provider = Create(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Cancellation was not propagated.");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (surface == "chat") await provider.CompleteChatAsync(new() { Model = "model" }, cts.Token);
            else if (surface == "responses") await provider.ResponsesAsync(new() { Model = "model" }, cts.Token);
            else await provider.MessagesAsync(new() { Model = "model", MaxTokens = 10, Messages = [] }, [], cts.Token);
        });
    }

    [Fact]
    public async Task Invalid_decision_distributions_are_not_success_shaped()
    {
        var provider = Create((_, _) => Task.FromResult(JsonResponse("""
            {"model":"Cloudflare/clef","answers":{"q":{"type":"choice","choice":"a","confidence":1,"probabilities":{"a":0.2}}},
             "usage":{"input_tokens":1,"output_tokens":0}}
            """)));
        var request = Decision();
        request.Questions["q"] = new DecisionChoiceQuestion { Criteria = new() { ["a"] = null, ["b"] = null } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(request));
    }

    [Fact]
    public void Registration_and_header_auth_are_wired()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Sference-Key"] = " test-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("SFERENCE"));
        Assert.True(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("Idempotency-Key", "sference"));
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader("X-Sference-Key", "sference"));
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(SferenceProvider));
    }

    private static DecisionRequest Decision() => new()
    {
        Model = "Cloudflare/clef", State = "input",
        Questions = new() { ["q"] = new DecisionBooleanQuestion() }
    };

    private static SferenceProvider NoNetwork()
        => Create((_, _) => throw new InvalidOperationException("Request should have failed before network I/O."));

    private static SferenceProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))));

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver(string? key = "test-key") : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "sference" ? key : null;
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
