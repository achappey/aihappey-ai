using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Messages;

public sealed class ToolSearchContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_tools_selection_omits_parallel_option_for_none(bool structured)
    {
        var request = new AIRequest
        {
            ProviderId = "anthropic",
            Model = "anthropic/test", Tools = [], ParallelToolCalls = false,
            MaxOutputTokens = 100,
            ToolChoice = structured ? JsonSerializer.SerializeToElement(new { type = "none" }) : "none"
        };
        var mapped = request.ToMessagesRequest("anthropic");
        Assert.Equal("none", mapped.ToolChoice?.Type);
        Assert.Null(mapped.ToolChoice?.DisableParallelToolUse);
        var json = JsonSerializer.SerializeToElement(mapped, MessagesJson.Default);
        Assert.Equal("{\"type\":\"none\"}", json.GetProperty("tool_choice").GetRawText());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Structured_tool_choice_preserves_name_and_parallel_setting(bool parallel, bool disable)
    {
        var request = new AIRequest
        {
            ProviderId = "anthropic",
            MaxOutputTokens = 100,
            ToolChoice = JsonSerializer.SerializeToElement(new { type = "function", name = "read_resource" }),
            ParallelToolCalls = parallel
        };
        var mapped = request.ToMessagesRequest("anthropic");
        Assert.Equal("tool", mapped.ToolChoice?.Type);
        Assert.Equal(disable, mapped.ToolChoice?.DisableParallelToolUse);
        Assert.Equal("read_resource", mapped.ToolChoice?.AdditionalProperties?["name"].GetString());
    }

    [Fact]
    public void Responses_search_result_retains_name_for_native_tool_references()
    {
        var request = new ResponseRequest
        {
            Model = "anthropic/test",
            MaxOutputTokens = 100,
            Input = new ResponseInput([
                new ResponseFunctionCallItem { CallId = "call_search", Name = "client_tool_search", Arguments = "{\"goal\":\"read mail\"}" },
                new ResponseFunctionCallOutputItem
                {
                    CallId = "call_search",
                    Output = "{\"structuredContent\":{\"selectedTools\":[{\"name\":\"read_resource\",\"inputSchema\":{\"type\":\"object\"}}]},\"isError\":false}"
                }
            ])
        };
        var mapped = request.ToUnifiedRequest("anthropic").ToMessagesRequest("anthropic");
        var result = Assert.Single(mapped.Messages.SelectMany(m => m.Content.Blocks ?? []), b => b.Type == "tool_result");
        var reference = Assert.Single(result.Content!.Blocks!);
        Assert.Equal("tool_reference", reference.Type);
        Assert.Equal("read_resource", reference.ToolName);
        Assert.Equal("call_search", result.ToolUseId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_search_never_becomes_successful_empty_tool_references(bool wrapped)
    {
        object failure = new { isError = true, structuredContent = new { selectedTools = Array.Empty<object>() }, content = new[] { new { type = "text", text = "Search failed" } } };
        if (wrapped) failure = new { output = failure };
        var request = new AIRequest
        {
            MaxOutputTokens = 100,
            ProviderId = "anthropic", Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "assistant", Content = [new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolName = "client_tool_search", ToolCallId = "failed_search",
                        Input = new { goal = "read mail" }, Output = failure
                    }]
                }]
            }
        };
        var mapped = request.ToMessagesRequest("anthropic");
        var result = Assert.Single(mapped.Messages.SelectMany(m => m.Content.Blocks ?? []), b => b.Type == "tool_result");
        Assert.True(result.IsError);
        Assert.DoesNotContain("tool_reference", JsonSerializer.Serialize(result));
        Assert.Contains("Search failed", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wrapped_search_results_preserve_multiple_references_and_call_correlation(bool empty)
    {
        object output = new { selectedTools = empty ? Array.Empty<object>() : new object[] { new { name = "read_resource" }, new { name = "search_resources" } } };
        var request = new AIRequest
        {
            MaxOutputTokens = 100,
            ProviderId = "anthropic", Input = new AIInput
            {
                Items =
                [
                    new AIInputItem { Role = "assistant", Content = [new AIToolCallContentPart { Type = "function_call", ToolName = "client_tool_search", ToolCallId = "search", Input = new { goal = "read mail" } }] },
                    new AIInputItem { Role = "tool", Content = [new AIToolCallContentPart { Type = "function_call_output", ToolCallId = "search", Output = new { output = new { structuredContent = output, isError = false } } }] }
                ]
            }
        };
        var mapped = request.ToMessagesRequest("anthropic");
        var result = Assert.Single(mapped.Messages.SelectMany(m => m.Content.Blocks ?? []), b => b.Type == "tool_result");
        Assert.Equal("search", result.ToolUseId);
        Assert.Equal(empty ? 0 : 2, result.Content!.Blocks!.Count);
        Assert.All(result.Content.Blocks, b => Assert.Equal("tool_reference", b.Type));
        Assert.False(result.IsError);
    }
}
