using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.ChatCompletions;

public sealed class ChatCompletionsUnifiedMapperStreamingTests
{
    [Fact]
    public void Typed_text_start_emits_only_an_assistant_role_choice()
    {
        var update = Event("text-start", new AITextStartEventData()).ToChatCompletionUpdate();

        var choice = Assert.Single(update.Choices);
        Assert.Equal(0, choice.Index);
        Assert.Null(choice.FinishReason);
        AssertJsonEqual(Element("""{"role":"assistant"}"""), choice.Delta);
        Assert.Equal("span-1", update.Id);
        Assert.Equal("chat.completion.chunk", update.Object);
        Assert.Equal("unknown", update.Model);
        Assert.Null(update.Usage);
        Assert.Null(update.AdditionalProperties);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n  ")]
    [InlineData("Hello")]
    [InlineData(" café 世界 🌍 e\u0301\n")]
    [InlineData("{\"literal\":\"text\"}")]
    public void Typed_text_delta_preserves_exact_content(string text)
    {
        var update = Event("text-delta", new AITextDeltaEventData { Delta = text }).ToChatCompletionUpdate();

        var choice = Assert.Single(update.Choices);
        Assert.Equal(0, choice.Index);
        Assert.Null(choice.FinishReason);
        Assert.Equal(text, choice.Delta.Content);
        AssertJsonEqual(JsonSerializer.SerializeToElement(new { content = text }), choice.Delta);
        Assert.Null(update.Usage);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("single")]
    [InlineData("multiple")]
    public void Chat_completion_unified_roundtrip_preserves_each_text_fragment_and_metadata(string entryPoint)
    {
        string[] fragments = ["Hello", " ", "\t\r\n", "café 世界 🌍", " e\u0301  "];
        var roundTrippedText = new List<string>();
        var state = new ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState();

        foreach (var fragment in fragments)
        {
            var original = new ChatCompletionUpdate
            {
                Id = "chat-1",
                Created = 1720000000,
                Model = "test-model",
                ServiceTier = "priority",
                Choices = [new() { Index = 0, Delta = new() { Content = fragment } }],
                AdditionalProperties = new() { ["future"] = Element("""{"nested":[1,true,null]}""") }
            };

            var unified = entryPoint switch
            {
                "json" => JsonSerializer.SerializeToElement(original, JsonSerializerOptions.Web).ToUnifiedStreamEvent("test"),
                "single" => original.ToUnifiedStreamEvent("test"),
                _ => Assert.Single(original.ToUnifiedStreamEvents("test", state))
            };
            Assert.Equal(fragment, Assert.IsType<AITextDeltaEventData>(unified.Event.Data).Delta);

            var result = unified.ToChatCompletionUpdate();
            var choice = Assert.Single(result.Choices);
            Assert.Equal(fragment, choice.Delta.Content);
            Assert.Equal(0, choice.Index);
            Assert.Null(choice.Delta.Role);
            Assert.Null(choice.FinishReason);
            Assert.Equal(original.Id, result.Id);
            Assert.Equal(original.Object, result.Object);
            Assert.Equal(original.Created, result.Created);
            Assert.Equal(original.Model, result.Model);
            Assert.Equal(original.ServiceTier, result.ServiceTier);
            AssertJsonEqual(original.AdditionalProperties["future"], result.AdditionalProperties!["future"]);
            Assert.Null(result.Usage);
            roundTrippedText.Add(choice.Delta.Content!);
        }

        Assert.Equal(string.Concat(fragments), string.Concat(roundTrippedText));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Typed_text_keeps_metadata_precedence_and_does_not_replay_raw_choices_or_usage(bool start)
    {
        var raw = Element("""
            {"id":"raw-id","model":"raw-model","choices":[{"index":7,"delta":{"content":"do not replay"}}],
             "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5},
             "future":{"nested":[1,true,null]}}
            """);
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.stream.id"] = Element("\"chat-1\""),
            ["chatcompletions.stream.object"] = "chat.completion.chunk",
            ["chatcompletions.stream.created"] = Element("1720000000"),
            ["chatcompletions.stream.model"] = "test-model",
            ["chatcompletions.stream.service_tier"] = "priority",
            ["chatcompletions.stream.raw"] = raw,
            ["unrelated"] = "not a chat completion extension"
        };
        object data = start ? new AITextStartEventData() : new AITextDeltaEventData { Delta = "typed text" };
        var update = Event(start ? "text-start" : "text-delta", data, metadata).ToChatCompletionUpdate();

        Assert.Equal("chat-1", update.Id);
        Assert.Equal("chat.completion.chunk", update.Object);
        Assert.Equal(1720000000, update.Created);
        Assert.Equal("test-model", update.Model);
        Assert.Equal("priority", update.ServiceTier);
        Assert.Null(update.Usage);
        Assert.Single(update.AdditionalProperties!);
        AssertJsonEqual(raw.GetProperty("future"), update.AdditionalProperties!["future"]);
        var choice = Assert.Single(update.Choices);
        Assert.Equal(0, choice.Index);
        Assert.Equal(start ? "assistant" : null, choice.Delta.Role);
        Assert.Equal(start ? null : "typed text", choice.Delta.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Raw_and_wrapped_json_keep_choices_tools_finish_usage_and_extensions(bool wrapped)
    {
        var raw = Element("""
            {"id":"raw-id","object":"chat.completion.chunk","created":1720000000,"model":"raw-model","service_tier":"priority",
             "choices":[
               {"index":2,"delta":{"role":"assistant","content":" raw text \n","reasoning_content":"reason","future_delta":true},"future_choice":7},
               {"index":4,"delta":{"tool_calls":[{"index":3,"id":"call-1","type":"function","provider_executed":true,
                 "function":{"name":"lookup","arguments":"{\"q\":","future_function":9}}]},"finish_reason":"tool_calls"}],
             "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,
               "prompt_tokens_details":{"cached_tokens":2,"future":3},"provider_usage":{"cost":0.000123}},
             "future":{"nested":[1,true,null]}}
            """);
        object data = wrapped ? new AIDataEventData { Data = raw } : raw;
        var update = Event("data-chat.completion.chunk", data,
            new() { ["chatcompletions.stream.id"] = "must-not-override-raw" }).ToChatCompletionUpdate();

        AssertJsonEqual(raw, update);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("error")]
    public void Existing_typed_tool_choices_remain_unchanged(string kind)
    {
        object data = kind switch
        {
            "input" => new AIToolInputAvailableEventData
            {
                ToolName = "lookup", Input = Element("""{"q":"value"}"""), ProviderExecuted = true
            },
            "output" => new AIToolOutputAvailableEventData
            {
                ToolName = "lookup", Output = Element("""{"answer":42}"""), ProviderExecuted = true, Preliminary = true
            },
            _ => new AIToolOutputErrorEventData { ToolCallId = "failed-call", ErrorText = "failed", ProviderExecuted = true }
        };
        var update = Event("tool-" + kind, data).ToChatCompletionUpdate();
        var choice = Assert.Single(update.Choices);
        Assert.Equal(0, choice.Index);
        Assert.Null(choice.FinishReason);
        Assert.Null(choice.Delta.Content);
        Assert.Null(choice.Delta.Role);
        var call = Assert.Single(choice.Delta.ToolCalls!);
        Assert.Equal(0, call.Index);
        Assert.Equal(kind == "error" ? "failed-call" : "span-1", call.Id);
        Assert.Equal("function", call.Type);
        Assert.True(call.AdditionalProperties!["provider_executed"].GetBoolean());
        var function = Assert.IsType<ChatCompletionFunctionCallDelta>(call.Function);
        if (kind == "input")
        {
            Assert.Equal("lookup", function.Name);
            Assert.Equal("{\"q\":\"value\"}", function.Arguments);
        }
        else if (kind == "output")
        {
            Assert.Equal("lookup", function.Name);
            AssertJsonEqual(Element("""{"answer":42}"""), function.AdditionalProperties!["output"]);
            Assert.True(function.AdditionalProperties["preliminary"].GetBoolean());
        }
        else
        {
            Assert.Null(function.Name);
            AssertJsonEqual(Element("""{"error":"failed"}"""), function.AdditionalProperties!["output"]);
        }
        Assert.Null(update.Usage);
    }

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("tool-calls", "tool_calls")]
    [InlineData("content-filter", "content-filter")]
    [InlineData(null, null)]
    public void Typed_finish_keeps_reason_mapping_model_fallback_and_null_usage(string? reason, string? expectedReason)
    {
        var finish = new AIFinishEventData
        {
            FinishReason = reason,
            Model = "finish-model",
            InputTokens = 10,
            OutputTokens = 5,
            TotalTokens = 15,
            MessageMetadata = AIFinishMessageMetadata.Create("finish-model", DateTimeOffset.UtcNow,
                usage: new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 })
        };

        var update = Event("finish", finish).ToChatCompletionUpdate();
        Assert.Equal("finish-model", update.Model);
        var choice = Assert.Single(update.Choices);
        Assert.Equal(0, choice.Index);
        Assert.Equal(expectedReason, choice.FinishReason);
        AssertJsonEqual(Element("{}"), choice.Delta);
        Assert.Null(update.Usage);

        var metadataUpdate = Event("finish", finish,
            new() { ["chatcompletions.stream.model"] = "metadata-model" }).ToChatCompletionUpdate();
        Assert.Equal("metadata-model", metadataUpdate.Model);
        Assert.Null(metadataUpdate.Usage);
    }

    [Fact]
    public void Other_typed_events_keep_empty_choices_without_fabricating_text_or_finish()
    {
        // This text-only fix does not define new Chat Completions encodings for
        // reasoning, incremental tools, sources, files, or other lifecycle events.
        (string Type, object? Data)[] events =
        [
            ("text-end", new AITextEndEventData()),
            ("reasoning-start", new AIReasoningStartEventData()),
            ("reasoning-delta", new AIReasoningDeltaEventData { Delta = "private reasoning" }),
            ("reasoning-end", new AIReasoningEndEventData()),
            ("tool-input-start", new AIToolInputStartEventData { ToolName = "lookup" }),
            ("tool-input-delta", new AIToolInputDeltaEventData { InputTextDelta = "{\"q\":" }),
            ("tool-approval-request", new AIToolApprovalRequestEventData { ApprovalId = "approval", ToolCallId = "call" }),
            ("source-url", new AISourceUrlEventData { SourceId = "source", Url = "https://example.test" }),
            ("file", new AIFileEventData { MediaType = "text/plain", Url = "https://example.test/file" }),
            ("error", new AIErrorEventData { ErrorText = "failed" }),
            ("abort", new AIAbortEventData { Reason = "cancelled" }),
            ("data-custom", new AIDataEventData { Data = "custom data" }),
            ("custom", new { text = "not a typed text delta" }),
            ("custom", null)
        ];

        foreach (var (type, data) in events)
        {
            var update = Event(type, data).ToChatCompletionUpdate();
            Assert.Empty(update.Choices);
            Assert.Null(update.Usage);
        }
    }

    private static AIStreamEvent Event(string type, object? data, Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = "test",
            Event = new AIEventEnvelope { Type = type, Id = "span-1", Data = data },
            Metadata = metadata
        };

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static void AssertJsonEqual<T>(JsonElement expected, T actual)
    {
        var actualJson = JsonSerializer.SerializeToElement(actual, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(expected, actualJson), $"Expected: {expected}\nActual: {actualJson}");
    }
}
