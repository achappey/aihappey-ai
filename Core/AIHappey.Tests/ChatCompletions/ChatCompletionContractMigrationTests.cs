using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.ChatCompletions;

public class ChatCompletionContractMigrationTests
{
    [Fact]
    public async Task Transport_preserves_native_metadata_and_raw_provider_options_separately()
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal("trace-1", body.GetProperty("metadata").GetProperty("trace").GetString());
            Assert.False(body.TryGetProperty("providerMetadata", out _));
            Assert.Equal(7, body.GetProperty("future_option").GetProperty("nested")[1].GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"chat-1","object":"chat.completion","created":1,"model":"native",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"answer"},"finish_reason":"stop"}],
                     "metadata":{"latency_ms":1.25,"native":{"trace":"kept"}}}
                    """, Encoding.UTF8, "application/json")
            };
        })) { BaseAddress = new Uri("https://example.test/") };
        var options = new ChatCompletionOptions
        {
            Model = "native",
            Metadata = new() { ["trace"] = "trace-1" },
            Messages = [new() { Role = "user", Content = "question" }]
        };
        options.SetProviderMetadata(new()
        {
            ["test"] = Element("""{"future_option":{"nested":[3,7]}}""")
        });

        var result = await client.GetChatCompletion(options, "test");
        Assert.Equal("answer", Assert.Single(result.Choices).Message.Content);
        var metadata = result.AdditionalProperties!["metadata"];
        Assert.Equal(1.25m, metadata.GetProperty("latency_ms").GetDecimal());
        Assert.Equal("kept", metadata.GetProperty("native").GetProperty("trace").GetString());
        Assert.Equal("trace-1", options.Metadata["trace"]);
    }

    [Fact]
    public void Provider_tools_preserve_function_custom_and_unknown_variants()
    {
        var raw = Element("""
            {"tools":[
              {"type":"function","function":{"name":"lookup","strict":true,"parameters":{"type":"object"},"future":7},"cache":{"mode":"read"}},
              {"type":"custom","custom":{"name":"grammar","format":{"type":"grammar","grammar":{"syntax":"lark","definition":"start: WORD"}},"future":true}},
              {"type":"provider_search","future":{"depth":3}}]}
            """);
        var metadata = new Dictionary<string, object?> { ["test"] = raw };
        var tools = metadata.GetChatCompletionToolDefinitions("test")!;

        Assert.Equal(3, tools.Count);
        Assert.IsType<ChatCompletionTool>(tools[0]);
        Assert.IsType<ChatCompletionCustomTool>(tools[1]);
        Assert.IsType<ChatCompletionUnknownTool>(tools[2]);
        Assert.True(JsonElement.DeepEquals(raw.GetProperty("tools"), JsonSerializer.SerializeToElement(tools, JsonSerializerOptions.Web)));
    }

    [Fact]
    public void Legacy_delta_preserves_indices_argument_fragments_and_extension_data()
    {
        var raw = Element("""
            {"reasoning_content":"reason","tool_calls":[
              {"index":4,"id":"call-a","type":"function","provider_executed":true,"function":{"name":"lookup","arguments":"{\"q\":","future":9}},
              {"index":7,"function":{"arguments":"\"value\"}"},"future":{"flag":true}}]}
            """);
        var delta = raw.ToChatCompletionDelta();
        var calls = delta.ToolCalls!.ToList();

        Assert.Equal(new[] { 4, 7 }, calls.Select(call => call.Index));
        Assert.Equal("{\"q\":", calls[0].Function!.Arguments);
        Assert.Equal("\"value\"}", calls[1].Function!.Arguments);
        Assert.True(calls[0].AdditionalProperties!["provider_executed"].GetBoolean());
        Assert.Equal(9, Assert.IsType<ChatCompletionFunctionCallDelta>(calls[0].Function).AdditionalProperties!["future"].GetInt32());
        Assert.True(calls[1].AdditionalProperties!["future"].GetProperty("flag").GetBoolean());
        Assert.Equal("reason", delta.AdditionalProperties!["reasoning_content"].GetString());
    }

    [Fact]
    public void Completed_tool_calls_without_indices_get_distinct_stream_indices()
    {
        var raw = Element("""
            {"tool_calls":[
              {"id":"a","type":"function","function":{"name":"one","arguments":"{}"}},
              {"id":"b","type":"function","function":{"name":"two","arguments":"{}"}}]}
            """);
        Assert.Equal(new[] { 0, 1 }, raw.ToChatCompletionDelta().ToolCalls!.Select(call => call.Index));
    }

    [Fact]
    public void Usage_preserves_decimal_cost_token_details_and_unknown_fields()
    {
        var raw = Element("""
            {"prompt_tokens":100,"completion_tokens":20,"total_tokens":120,
             "cost":0.00046988999999999993,
             "prompt_tokens_details":{"cached_tokens":30,"cache_write_tokens":5,"future":11},
             "completion_tokens_details":{"reasoning_tokens":7,"future":13},
             "provider_breakdown":{"native":[1,2,3]}}
            """);
        var usage = raw.ToChatCompletionUsage()!;
        Assert.Equal(30, usage.PromptTokensDetails!.CachedTokens);
        Assert.Equal(5, usage.PromptTokensDetails.CacheWriteTokens);
        Assert.Equal(7, usage.CompletionTokensDetails!.ReasoningTokens);
        Assert.Equal(0.00046988999999999993m, usage.AdditionalProperties!["cost"].GetDecimal());
        Assert.True(JsonElement.DeepEquals(raw, JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)));
    }

    [Fact]
    public void Typed_usage_costing_retains_existing_token_and_cache_formula()
    {
        var usage = new ChatCompletionUsage
        {
            PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15,
            PromptTokensDetails = new() { CachedTokens = 2, CacheWriteTokens = 3 }
        };
        var pricing = new ModelPricing { Input = 1, Output = 2, InputCacheRead = 3, InputCacheWrite = 4 };
        var metadata = ModelCostMetadataEnricher.AddCostFromUsage(usage, null, pricing);
        var gateway = Assert.IsType<Dictionary<string, object?>>(metadata["gateway"]);
        Assert.Equal(38m, Assert.IsType<decimal>(gateway["cost"]));
        Assert.Equal(15, ModelCostMetadataEnricher.GetTotalTokens(usage));
    }

    [Fact]
    public void Unified_usage_round_trip_keeps_raw_cost_and_extended_token_details()
    {
        var raw = Element("""
            {"id":"one","created":1,"model":"native","choices":[],
             "usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120,
               "cost":0.0000001234567890123456789,
               "prompt_tokens_details":{"cached_tokens":9,"cache_write_tokens":3,"future":7},
               "completion_tokens_details":{"reasoning_tokens":4},"native":{"billed":true}}}
            """);
        var unified = raw.ToUnifiedResponse("test");
        var usage = Assert.IsType<AIUsage>(unified.Usage);
        Assert.Equal(3, usage.CacheWriteInputTokens);
        var completion = unified.ToChatCompletion();
        Assert.True(JsonElement.DeepEquals(raw.GetProperty("usage"), JsonSerializer.SerializeToElement(completion.Usage, JsonSerializerOptions.Web)));
    }

    [Fact]
    public void Named_and_allowed_tool_choices_are_not_normalized_to_auto()
    {
        foreach (var raw in new[]
        {
            Element("""{"type":"function","function":{"name":"lookup"},"future":7}"""),
            Element("""{"type":"allowed_tools","allowed_tools":{"mode":"required","tools":[{"type":"function","function":{"name":"lookup"}}]},"future":true}""")
        })
        {
            var request = new AIRequest { ProviderId = "test", Model = "native", ToolChoice = raw };
            var options = request.ToChatCompletionOptions("test");
            Assert.True(JsonElement.DeepEquals(raw, JsonSerializer.SerializeToElement(options.ToolChoiceOption, JsonSerializerOptions.Web)));
        }
    }

    [Fact]
    public void Json_null_tool_choice_remains_an_absent_choice()
    {
        var request = new AIRequest { ProviderId = "test", Model = "native", ToolChoice = Element("null") };
        var options = request.ToChatCompletionOptions("test");
        Assert.Equal("none", Assert.IsType<ChatCompletionToolChoiceMode>(options.ToolChoiceOption).Mode);
    }

    [Fact]
    public void Provider_tools_are_materialized_once_without_losing_other_provider_options()
    {
        var request = new AIRequest
        {
            ProviderId = "test", Model = "native",
            Metadata = new()
            {
                ["test"] = Element("""{"tools":[{"type":"provider_search","future":7}],"headers":{"x-native":"kept"},"future_option":{"depth":3}}""")
            }
        };
        var options = request.ToChatCompletionOptions("test");
        Assert.IsType<ChatCompletionUnknownTool>(Assert.Single(options.Tools));
        var providers = options.GetProviderMetadata();
        Assert.Null(providers.GetChatCompletionToolDefinitions("test"));
        var provider = JsonSerializer.SerializeToElement(providers["test"]);
        Assert.Equal("kept", provider.GetProperty("headers").GetProperty("x-native").GetString());
        Assert.Equal(3, provider.GetProperty("future_option").GetProperty("depth").GetInt32());
    }

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
