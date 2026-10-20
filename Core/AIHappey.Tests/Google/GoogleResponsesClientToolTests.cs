using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Tests.Google;

[CollectionDefinition("Google Responses streaming", DisableParallelization = true)]
public sealed class GoogleResponsesStreamingCollection;

[Collection("Google Responses streaming")]
public sealed class GoogleResponsesClientToolTests
{
    private const string ToolName = "mcp_github_rest_countries_search_codes_223eb8ccbbcfd74a";

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ResponsesStreamCompletesClientCallsWithoutProviderResults(int callCount)
    {
        var events = new List<string>
        {
            """{"event_type":"interaction.created","interaction":{"id":"i-client","status":"in_progress"}}""",
            """{"event_type":"step.start","index":0,"step":{"type":"thought","signature":"opaque-thought-signature"}}""",
            """{"event_type":"step.stop","index":0}"""
        };
        for (var index = 1; index <= callCount; index++)
            events.Add(JsonSerializer.Serialize(new
            {
                event_type = "step.start", index,
                step = new { type = "function_call", id = $"call_{index}", name = ToolName, arguments = new { } }
            }));
        // Complete in reverse order to exercise per-item state, not just sequential calls.
        for (var index = callCount; index >= 1; index--)
        {
            events.Add(JsonSerializer.Serialize(new
            {
                event_type = "step.delta", index,
                delta = new { type = "arguments_delta", arguments = "{\"name\":" }
            }));
            events.Add(JsonSerializer.Serialize(new
            {
                event_type = "step.delta", index,
                delta = new { type = "arguments_delta", arguments = "\"Poland\"}" }
            }));
            events.Add(JsonSerializer.Serialize(new { event_type = "step.stop", index }));
        }
        events.Add("""{"event_type":"interaction.completed","interaction":{"id":"i-client","model":"gemini-3.5-flash","status":"completed","usage":{"total_input_tokens":528,"total_output_tokens":38,"total_tokens":566}}}""");

        var handler = new GoogleClientToolTests.RecordingHandler(GoogleClientToolTests.SseResponse(events.ToArray()));
        var parts = await Stream(handler);
        var completed = Assert.IsType<ResponseCompleted>(parts[^1]);
        var reasoning = Assert.Single(parts.OfType<ResponseOutputItemDone>(), part => part.Item.Type == "reasoning");
        Assert.Equal("opaque-thought-signature", reasoning.Item.AdditionalProperties!["encrypted_content"].GetString());
        Assert.Equal("opaque-thought-signature", reasoning.Item.AdditionalProperties["provider_metadata"].GetProperty("google").GetProperty("signature").GetString());

        Assert.Equal(callCount, parts.OfType<ResponseFunctionCallArgumentsDone>().Count());
        Assert.Equal(callCount, parts.OfType<ResponseOutputItemDone>().Count(part => part.Item.Type == "function_call"));
        for (var index = 1; index <= callCount; index++)
        {
            var id = $"call_{index}";
            var added = Assert.Single(parts.OfType<ResponseOutputItemAdded>(), part => part.Item.Id == id);
            var arguments = Assert.Single(parts.OfType<ResponseFunctionCallArgumentsDone>(), part => part.ItemId == id);
            var done = Assert.Single(parts.OfType<ResponseOutputItemDone>(), part => part.Item.Id == id);
            Assert.Equal(index, added.OutputIndex);
            Assert.Equal(added.OutputIndex, arguments.OutputIndex);
            Assert.Equal(added.OutputIndex, done.OutputIndex);
            Assert.Equal(id, done.Item.CallId);
            Assert.Equal(ToolName, done.Item.Name);
            Assert.Equal("completed", done.Item.Status);
            Assert.Equal("{\"name\":\"Poland\"}", arguments.Arguments);
            Assert.Equal(arguments.Arguments, done.Item.Arguments?.GetString());
            Assert.False(done.Item.AdditionalProperties!["provider_executed"].GetBoolean());
            Assert.True(parts.IndexOf(added) < parts.IndexOf(arguments));
            Assert.True(parts.IndexOf(arguments) < parts.IndexOf(done));
            Assert.True(parts.IndexOf(done) < parts.IndexOf(completed));
            var finalItem = JsonSerializer.SerializeToElement(completed.Response.Output.ElementAt(index), ResponseJson.Default);
            Assert.Equal(id, finalItem.GetProperty("id").GetString());
            Assert.Equal(done.Item.Arguments?.GetString(), finalItem.GetProperty("arguments").GetString());
        }

        var sequenceNumbers = parts.Select(part => JsonSerializer.SerializeToElement(part, ResponseJson.Default)
            .GetProperty("sequence_number").GetInt32()).ToArray();
        Assert.Equal(sequenceNumbers.OrderBy(number => number).Distinct(), sequenceNumbers);
        Assert.Equal(528, completed.Response.NormalizedUsage?.InputTokens);
        Assert.Equal(38, completed.Response.NormalizedUsage?.OutputTokens);
        Assert.Equal(566, completed.Response.NormalizedUsage?.TotalTokens);
        var metadata = JsonSerializer.SerializeToElement(completed.Response.Metadata, ResponseJson.Default);
        Assert.True(metadata.GetProperty("gateway").GetProperty("cost").GetDecimal() > 0);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Contains(body.RootElement.GetProperty("tools").EnumerateArray(), tool =>
            tool.TryGetProperty("name", out var name) && name.GetString() == ToolName);
    }

    [Fact]
    public async Task ResponsesStreamKeepsNativeToolProviderExecutedAndWaitsForItsResult()
    {
        var handler = new GoogleClientToolTests.RecordingHandler(GoogleClientToolTests.SseResponse(
            """{"event_type":"interaction.created","interaction":{"id":"i-native","status":"in_progress"}}""",
            """{"event_type":"step.start","index":0,"step":{"type":"function_call","id":"native-1","name":"write_file","arguments":{"path":"result.txt"}}}""",
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"step.start","index":1,"step":{"type":"function_result","call_id":"native-1","name":"write_file","result":{"ok":true}}}""",
            """{"event_type":"step.stop","index":1}""",
            """{"event_type":"interaction.completed","interaction":{"id":"i-native","status":"completed"}}"""));
        var parts = await Stream(handler);
        Assert.Empty(parts.OfType<ResponseFunctionCallArgumentsDone>());
        var done = Assert.Single(parts.OfType<ResponseOutputItemDone>());
        Assert.Equal("custom_tool_call", done.Item.Type);
        Assert.True(done.Item.AdditionalProperties!["provider_executed"].GetBoolean());
        Assert.True(done.Item.AdditionalProperties["output"].GetProperty("ok").GetBoolean());
        Assert.True(parts.IndexOf(Assert.Single(parts.OfType<ResponseCustomToolCallInputDone>())) < parts.IndexOf(done));
    }

    private static async Task<List<ResponseStreamPart>> Stream(GoogleClientToolTests.RecordingHandler handler)
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>($$$"""
            {"model":"google/gemini-3.5-flash","input":"zoek alle over poland op github restcountries",
             "stream":true,"parallel_tool_calls":true,"tool_choice":"auto",
             "metadata":{"google":{"tools":[{"type":"google_maps"}]}},
             "tools":[{"type":"function","name":"{{{ToolName}}}","parameters":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}]}
            """, ResponseJson.Default)!;
        var parts = new List<ResponseStreamPart>();
        await foreach (var part in GoogleClientToolTests.CreateProvider(handler).ResponsesStreamingAsync(request))
            parts.Add(part);
        return parts;
    }
}
