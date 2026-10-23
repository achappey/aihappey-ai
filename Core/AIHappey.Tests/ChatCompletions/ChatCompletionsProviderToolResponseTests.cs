using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.ChatCompletions;

public sealed class ChatCompletionsProviderToolResponseTests
{
    [Theory]
    [InlineData(null, "stop")]
    [InlineData("tool_calls", "stop")]
    [InlineData("function_call", "stop")]
    [InlineData("length", "length")]
    [InlineData("content_filter", "content_filter")]
    public void Completed_provider_tool_responses_preserve_input_output_and_normal_finish_reason(
        string? originalFinishReason, string expectedFinishReason)
    {
        var response = Response(finishReason: originalFinishReason);
        var original = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);

        var completion = JsonSerializer.SerializeToElement(response.ToChatCompletion(), JsonSerializerOptions.Web);

        var choice = Assert.Single(completion.GetProperty("choices").EnumerateArray());
        Assert.Equal(expectedFinishReason, choice.GetProperty("finish_reason").GetString());
        var message = choice.GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind);
        var call = Assert.Single(message.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("provider-1", call.GetProperty("id").GetString());
        Assert.True(call.GetProperty("provider_executed").GetBoolean());
        var function = call.GetProperty("function");
        Assert.Equal("run_task", function.GetProperty("name").GetString());
        using var arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
        Assert.Equal("acme/repo", arguments.RootElement.GetProperty("repo").GetString());
        Assert.Equal("task-1", function.GetProperty("output").GetProperty("task_id").GetString());
        Assert.Equal(original, JsonSerializer.Serialize(response, JsonSerializerOptions.Web));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Response_round_trip_does_not_replay_completed_provider_tools_but_preserves_client_calls(bool includeClient)
    {
        var response = Response(includeClient, "tool_calls");
        var completion = JsonSerializer.SerializeToElement(response.ToChatCompletion(), JsonSerializerOptions.Web);
        var choice = completion.GetProperty("choices")[0];
        Assert.Equal(includeClient ? "tool_calls" : "stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(includeClient ? 2 : 1, choice.GetProperty("message").GetProperty("tool_calls").GetArrayLength());
        var rawRequest = JsonSerializer.SerializeToElement(new
        {
            model = "next-model",
            messages = new[] { choice.GetProperty("message") }
        }, JsonSerializerOptions.Web);

        var unified = rawRequest.ToUnifiedRequest("next-provider");
        var tools = unified.Input!.Items!.SelectMany(item => item.Content!).OfType<AIToolCallContentPart>().ToList();
        var providerTool = Assert.Single(tools, tool => tool.ProviderExecuted == true);
        Assert.Equal("provider-1", providerTool.ToolCallId);
        Assert.Equal("output-available", providerTool.State);
        Assert.Equal("task-1", Assert.IsType<JsonElement>(providerTool.Output).GetProperty("task_id").GetString());

        var replay = JsonSerializer.SerializeToElement(unified.ToChatCompletionOptions("next-provider"), JsonSerializerOptions.Web);
        foreach (var message in replay.GetProperty("messages").EnumerateArray())
        {
            Assert.NotEqual("tool", message.GetProperty("role").GetString());
            if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null)
                continue;
            Assert.True(includeClient);
            var call = Assert.Single(calls.EnumerateArray());
            Assert.Equal("client-1", call.GetProperty("id").GetString());
            Assert.Equal("lookup", call.GetProperty("function").GetProperty("name").GetString());
        }
        var replayJson = replay.GetRawText();
        Assert.DoesNotContain("provider-1", replayJson);
        Assert.DoesNotContain("task-1", replayJson);
        if (includeClient) Assert.Contains("client-1", replayJson);
    }

    [Theory]
    [InlineData("provider_executed", false)]
    [InlineData("providerExecuted", false)]
    [InlineData("provider_executed", true)]
    [InlineData("providerExecuted", true)]
    public void Incoming_provider_markers_are_respected_without_resurrecting_raw_calls(string marker, bool nested)
    {
        var function = new Dictionary<string, object?>
        {
            ["name"] = "run_task", ["arguments"] = "{}", ["output"] = new { task_id = "task-1" }
        };
        var call = new Dictionary<string, object?> { ["id"] = "provider-1", ["type"] = "function", ["function"] = function };
        (nested ? function : call)[marker] = true;
        var rawRequest = JsonSerializer.SerializeToElement(new
        {
            model = "next-model",
            messages = new[] { new { role = "assistant", content = "Done", tool_calls = new[] { call } } }
        }, JsonSerializerOptions.Web);

        var unified = rawRequest.ToUnifiedRequest("next-provider");
        var replay = JsonSerializer.SerializeToElement(unified.ToChatCompletionOptions("next-provider"), JsonSerializerOptions.Web);

        Assert.DoesNotContain("provider-1", replay.GetRawText());
        Assert.Contains("Done", replay.GetRawText());
    }

    [Theory]
    [InlineData("output-available")]
    [InlineData("output-error")]
    public void Completed_provider_tools_with_explicit_null_output_are_not_lost(string state)
    {
        var response = Response(state: state, nullOutput: true);

        var completion = JsonSerializer.SerializeToElement(response.ToChatCompletion(), JsonSerializerOptions.Web);

        var choice = completion.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        var call = Assert.Single(choice.GetProperty("message").GetProperty("tool_calls").EnumerateArray());
        Assert.True(call.GetProperty("provider_executed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, call.GetProperty("function").GetProperty("output").ValueKind);
    }

    private static AIResponse Response(bool includeClient = false, string? finishReason = null,
        string state = "output-available", bool nullOutput = false)
    {
        var content = new List<AIContentPart>
        {
            new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "provider-1", ToolName = "run_task", ProviderExecuted = true,
                State = state, Input = new { repo = "acme/repo" }, Output = nullOutput ? null : new { task_id = "task-1" }
            }
        };
        if (includeClient)
            content.Add(new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "client-1", ToolName = "lookup", ProviderExecuted = false,
                State = "input-available", Input = new { query = "weather" }
            });
        return new AIResponse
        {
            ProviderId = "provider", Model = "model", Status = "completed",
            Output = new AIOutput { Items = [new AIOutputItem
            {
                Type = "message", Role = "assistant", Content = content,
                Metadata = new() { ["chatcompletions.choice.finish_reason"] = finishReason }
            }] }
        };
    }
}
