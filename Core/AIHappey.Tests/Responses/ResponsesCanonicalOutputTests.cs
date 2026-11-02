using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Output;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesCanonicalOutputTests
{
    [Theory]
    [InlineData("completed", "completed")]
    [InlineData("in_progress", "in_progress")]
    [InlineData("failed", "incomplete")]
    public void Constructed_messages_have_canonical_identity_status_and_content(string status, string itemStatus)
    {
        var response = Response(status, new AIOutputItem
        {
            Content = [new AITextContentPart { Type = "text", Text = "Hello" }],
            Metadata = new() { ["id"] = "msg_runtime" }
        }).ToResponseResult();

        var message = Assert.IsType<ResponseOutputMessage>(Assert.Single(response.Output));
        Assert.Equal("msg_runtime", message.Id);
        Assert.Equal(ResponseRole.Assistant, message.Role);
        Assert.Equal(itemStatus, message.Status);
        var text = Assert.IsType<ResponseOutputText>(Assert.Single(message.Content));
        Assert.Equal("Hello", text.Text);
        Assert.Empty(text.Annotations);
        AssertCanonical(response);
        Assert.Equal("Hello", response.Output.GetAssistantOutputText());
        Assert.IsType<ResponseInputMessage>(Assert.Single(response.OutputItems));
    }

    [Fact]
    public void Constructed_reasoning_is_not_dropped_or_disguised_as_an_empty_message()
    {
        var result = Response("completed", new AIOutputItem
        {
            Type = "reasoning",
            Content = [new AIReasoningContentPart { Type = "reasoning", Text = "Compare sources", Signature = "opaque" }],
            Metadata = new() { ["id"] = "rs_runtime" }
        }).ToResponseResult();

        var reasoning = Assert.IsType<ResponseOutputReasoningItem>(Assert.Single(result.Output));
        Assert.Equal("rs_runtime", reasoning.Id);
        Assert.Equal("Compare sources", Assert.Single(reasoning.Content!).Text);
        Assert.NotNull(reasoning.Summary);
        Assert.Equal("opaque", reasoning.EncryptedContent);
        AssertCanonical(result);
    }

    [Fact]
    public void Constructed_tool_pairs_have_distinct_item_ids_shared_call_identity_and_lossless_payloads()
    {
        var result = Response("completed", new AIOutputItem
        {
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "call_runtime", ToolName = "lookup", State = "output-available",
                Input = new { query = "news" }, Output = new { results = new[] { "one", "two" } }
            }]
        }).ToResponseResult();

        Assert.Equal(2, result.Output.Count());
        var call = Assert.IsType<ResponseOutputItem<ResponseFunctionCallItem>>(result.Output.First()).Item;
        var output = Assert.IsType<ResponseOutputFunctionCallResult>(result.Output.Last());
        Assert.False(string.IsNullOrWhiteSpace(call.Id));
        Assert.NotEqual(call.Id, output.Id);
        Assert.Equal("call_runtime", call.CallId);
        Assert.Equal(call.CallId, output.CallId);
        Assert.Equal("completed", call.Status);
        Assert.Equal("news", JsonDocument.Parse(call.Arguments).RootElement.GetProperty("query").GetString());
        Assert.Equal(2, JsonDocument.Parse(output.Output.Text!).RootElement.GetProperty("results").GetArrayLength());
        AssertCanonical(result);
    }

    [Fact]
    public void Native_replay_outputs_are_not_reconstructed_or_stripped()
    {
        const string native = """{"type":"message","id":"native","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Original","annotations":[],"x_text":true}],"phase":"commentary","x_future":{"v":[1,null]}}""";
        var raw = JsonSerializer.Deserialize<JsonElement>(native);
        var result = Response("completed", new AIOutputItem
        {
            Content = [new AITextContentPart { Type = "text", Text = "Original" }],
            Metadata = new() { ["responses.raw_output"] = raw }
        }).ToResponseResult();

        Assert.Equal(native, Assert.IsType<JsonElement>(Assert.Single(result.Output)).GetRawText());
    }

    [Fact]
    public void Streaming_completion_contains_typed_reasoning_and_message_contracts()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        var events = new[]
        {
            Event("reasoning-start", "rs_stream", new AIReasoningStartEventData { Signature = "opaque" }),
            Event("reasoning-delta", "rs_stream", new AIReasoningDeltaEventData { Delta = "Think" }),
            Event("reasoning-end", "rs_stream", new AIReasoningEndEventData()),
            Event("text-start", "msg_stream", new AITextStartEventData()),
            Event("text-delta", "msg_stream", new AITextDeltaEventData { Delta = "Answer" }),
            Event("text-end", "msg_stream", new AITextEndEventData()),
            Event("finish", "resp_stream", new AIFinishEventData { FinishReason = "stop", Model = "test" })
        };
        var parts = events.Select(e => e.ToResponseStreamPart(state)).ToList();
        var result = Assert.IsType<ResponseCompleted>(parts[^1]).Response;
        var reasoning = Assert.IsType<ResponseOutputReasoningItem>(result.Output.First());
        Assert.NotNull(reasoning.Summary);
        Assert.Equal("Think", Assert.Single(reasoning.Content!).Text);
        Assert.Equal("opaque", reasoning.EncryptedContent);
        var message = Assert.IsType<ResponseOutputMessage>(result.Output.Last());
        Assert.Equal("msg_stream", message.Id);
        Assert.Empty(Assert.IsType<ResponseOutputText>(Assert.Single(message.Content)).Annotations);
        AssertCanonical(result);

        foreach (var done in parts.OfType<ResponseOutputItemDone>())
        {
            var json = JsonSerializer.SerializeToElement(done.Item, ResponseJson.Default);
            Assert.NotNull(json.Deserialize<ResponseOutputItem>(ResponseJson.Default));
        }
    }

    [Fact]
    public void Constructed_content_preserves_files_unknown_annotations_and_extension_data()
    {
        var response = Response("completed", new AIOutputItem
        {
            Content = [new AITextContentPart
            {
                Type = "text", Text = "Report", Metadata = new()
                {
                    ["responses.annotations"] = JsonSerializer.Deserialize<JsonElement>("""[{"type":"future_citation","source":{"id":"source_1"}}]"""),
                    ["responses.additional_properties"] = new Dictionary<string, JsonElement> { ["x_text"] = JsonSerializer.SerializeToElement(true) }
                }
            }, new AIFileContentPart { Type = "file", MediaType = "application/pdf", Filename = "report.pdf", Data = new byte[] { 1, 2, 3 } }]
        }).ToResponseResult();
        var message = Assert.IsType<ResponseOutputMessage>(Assert.Single(response.Output));
        var text = Assert.IsType<ResponseOutputText>(message.Content[0]);
        Assert.IsType<ResponseUnknownOutputAnnotation>(Assert.Single(text.Annotations));
        Assert.True(text.AdditionalProperties!["x_text"].GetBoolean());
        var file = Assert.IsType<ResponseUnknownOutputContent>(message.Content[1]);
        Assert.Equal("data:application/pdf;base64,AQID", file.AdditionalProperties!["file_data"].GetString());
        Assert.Equal("report.pdf", file.AdditionalProperties["filename"].GetString());
        AssertCanonical(response);
    }

    [Fact]
    public void Provider_tools_use_runtime_specific_canonical_fields()
    {
        var code = Response("completed", new AIOutputItem
        {
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "ci_1", ToolName = "code_interpreter", ProviderExecuted = true,
                Input = new { code = "print(1)" },
                Output = new { container_id = "cntr_1", outputs = new[] { new { type = "logs", logs = "1" } } }
            }]
        }).ToResponseResult();
        var interpreter = Assert.IsType<ResponseOutputCodeInterpreterCall>(Assert.Single(code.Output));
        Assert.Equal("cntr_1", interpreter.ContainerId);
        Assert.Equal("print(1)", interpreter.Code);
        Assert.Equal("1", Assert.IsType<ResponseOutputCodeInterpreterLogs>(Assert.Single(interpreter.Outputs!)).Logs);
        AssertCanonical(code);

        var mcp = Response("completed", new AIOutputItem
        {
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "mcp_1", ToolName = "mcp_call", ProviderExecuted = true,
                Input = new { query = "docs" }, Output = "Found documentation",
                Metadata = new() { ["test"] = new Dictionary<string, object?> { ["server_label"] = "docs", ["name"] = "search" } }
            }]
        }).ToResponseResult();
        var call = Assert.IsType<ResponseOutputMcpCall>(Assert.Single(mcp.Output));
        Assert.Equal("docs", call.ServerLabel);
        Assert.Equal("search", call.Name);
        Assert.Equal("Found documentation", call.Output);
        AssertCanonical(mcp);
    }

    [Fact]
    public void Streaming_sources_and_files_survive_in_canonical_completion_content()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        var events = new[]
        {
            Event("text-start", "msg_1", new AITextStartEventData()),
            Event("text-delta", "msg_1", new AITextDeltaEventData { Delta = "Answer" }),
            Event("source-url", "msg_1", new AISourceUrlEventData
            {
                SourceId = "source_1", Url = "https://example.test/source", Title = "Source", StartIndex = 0, EndIndex = 6
            }),
            Event("text-end", "msg_1", new AITextEndEventData()),
            Event("file", "file_1", new AIFileEventData { MediaType = "application/pdf", Filename = "report.pdf", Url = "https://files.test/report?sig=a%2Fb" }),
            Event("finish", "resp_1", new AIFinishEventData { FinishReason = "stop", Model = "test" })
        };
        var parts = events.Select(e => e.ToResponseStreamPart(state)).ToList();
        var result = Assert.IsType<ResponseCompleted>(parts[^1]).Response;
        var message = Assert.IsType<ResponseOutputMessage>(result.Output.First());
        var citation = Assert.IsType<ResponseOutputUrlCitation>(Assert.Single(Assert.IsType<ResponseOutputText>(message.Content[0]).Annotations));
        Assert.Equal("Source", citation.Title);
        Assert.Equal(6, citation.EndIndex);
        var fileMessage = Assert.IsType<ResponseOutputMessage>(result.Output.Last());
        var file = Assert.IsType<ResponseUnknownOutputContent>(Assert.Single(fileMessage.Content));
        Assert.Equal("https://files.test/report?sig=a%2Fb", file.AdditionalProperties!["file_url"].GetString());
        AssertCanonical(result);
    }

    [Fact]
    public void Streaming_function_results_have_a_canonical_result_item_and_preserve_call_linkage()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        var events = new[]
        {
            Event("tool-input-start", "call_1", new AIToolInputStartEventData { ToolName = "lookup", ProviderExecuted = false }),
            Event("tool-input-available", "call_1", new AIToolInputAvailableEventData { ToolName = "lookup", Input = new { query = "news" } }),
            Event("tool-output-available", "call_1", new AIToolOutputAvailableEventData { ToolName = "lookup", Output = "result", ProviderExecuted = false }),
            Event("finish", "resp_1", new AIFinishEventData { FinishReason = "stop", Model = "test" })
        };
        var parts = events.Select(e => e.ToResponseStreamPart(state)).ToList();
        var result = Assert.IsType<ResponseCompleted>(parts[^1]).Response;
        var call = Assert.IsType<ResponseOutputItem<ResponseFunctionCallItem>>(result.Output.First()).Item;
        var output = Assert.IsType<ResponseOutputFunctionCallResult>(result.Output.Last());
        Assert.Equal(call.CallId, output.CallId);
        Assert.NotEqual(call.Id, output.Id);
        Assert.Equal("result", output.Output.Text);
        AssertCanonical(result);
    }

    [Fact]
    public void Interrupted_stream_does_not_report_unfinished_message_as_completed()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        _ = Event("text-start", "msg_1", new AITextStartEventData()).ToResponseStreamPart(state);
        _ = Event("text-delta", "msg_1", new AITextDeltaEventData { Delta = "Partial" }).ToResponseStreamPart(state);
        var completed = Assert.IsType<ResponseIncompleteEvent>(Event("finish", "resp_1",
            new AIFinishEventData { FinishReason = "length", Model = "test" }).ToResponseStreamPart(state));
        Assert.Equal("incomplete", completed.Response.Status);
        Assert.Equal("max_output_tokens", completed.Response.IncompleteDetails!.Value.GetProperty("reason").GetString());
        Assert.Equal("incomplete", Assert.IsType<ResponseOutputMessage>(Assert.Single(completed.Response.Output)).Status);
        AssertCanonical(completed.Response);
    }

    [Theory]
    [InlineData("in_progress", "in_progress")]
    [InlineData("output-error", "failed")]
    public void Image_calls_without_a_result_still_have_a_canonical_nullable_result(string state, string status)
    {
        var response = Response("in_progress", new AIOutputItem
        {
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "image_runtime", ToolName = "generate_image", ProviderExecuted = true,
                State = state, Input = new { prompt = "Sky" }
            }]
        }).ToResponseResult();
        var image = Assert.IsType<ResponseOutputImageGenerationCall>(Assert.Single(response.Output));
        Assert.Equal("image_runtime", image.Id);
        Assert.Equal(status, image.Status);
        Assert.Null(image.Result);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(image, ResponseJson.Default).GetProperty("result").ValueKind);
        AssertCanonical(response);
    }

    [Fact]
    public void Mixed_message_and_reasoning_content_get_distinct_item_identities()
    {
        var response = Response("completed", new AIOutputItem
        {
            Metadata = new() { ["id"] = "msg_runtime" },
            Content = [new AIReasoningContentPart { Type = "reasoning", Text = "Think" }, new AITextContentPart { Type = "text", Text = "Answer" }]
        }).ToResponseResult();
        Assert.Equal("msg_runtime:reasoning", Assert.IsType<ResponseOutputReasoningItem>(response.Output.First()).Id);
        Assert.Equal("msg_runtime", Assert.IsType<ResponseOutputMessage>(response.Output.Last()).Id);
        AssertCanonical(response);
    }

    [Fact]
    public void Failed_finish_uses_the_existing_failure_event_contract()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        _ = Event("text-start", "msg_1", new AITextStartEventData()).ToResponseStreamPart(state);
        var failed = Assert.IsType<ResponseFailed>(Event("finish", "resp_1",
            new AIFinishEventData { FinishReason = "error", Model = "test" }).ToResponseStreamPart(state));
        Assert.Equal("failed", failed.Response.Status);
        Assert.Equal("incomplete", Assert.IsType<ResponseOutputMessage>(Assert.Single(failed.Response.Output)).Status);
    }

    [Fact]
    public void Streaming_native_replay_bypasses_canonical_reconstruction()
    {
        const string native = """{"type":"message","id":"native_msg","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Original","annotations":[]}],"x_future":{"v":1}}""";
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        _ = Event("text-start", "native_msg", new AITextStartEventData
        {
            ProviderMetadata = new() { ["test"] = new Dictionary<string, object>
            {
                ["responses_item"] = JsonSerializer.Deserialize<JsonElement>(native)
            } }
        }).ToResponseStreamPart(state);
        _ = Event("text-delta", "native_msg", new AITextDeltaEventData { Delta = "Original" }).ToResponseStreamPart(state);
        var completed = Assert.IsType<ResponseCompleted>(Event("finish", "resp_1",
            new AIFinishEventData { FinishReason = "stop", Model = "test" }).ToResponseStreamPart(state));
        Assert.Equal(native, Assert.IsType<JsonElement>(Assert.Single(completed.Response.Output)).GetRawText());
    }

    [Fact]
    public void Missing_stream_item_ids_are_allocated_once_and_reused_for_deltas_and_completion()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        var start = Assert.IsType<ResponseOutputItemAdded>(Event("text-start", null, new AITextStartEventData()).ToResponseStreamPart(state));
        var delta = Assert.IsType<ResponseOutputTextDelta>(Event("text-delta", null, new AITextDeltaEventData { Delta = "Answer" }).ToResponseStreamPart(state));
        var end = Assert.IsType<ResponseOutputItemDone>(Event("text-end", null, new AITextEndEventData()).ToResponseStreamPart(state));
        var finish = Assert.IsType<ResponseCompleted>(Event("finish", "resp_1",
            new AIFinishEventData { FinishReason = "stop", Model = "test" }).ToResponseStreamPart(state));
        Assert.False(string.IsNullOrWhiteSpace(start.Item.Id));
        Assert.Equal(start.Item.Id, delta.ItemId);
        Assert.Equal(start.Item.Id, end.Item.Id);
        Assert.Equal(start.Item.Id, Assert.IsType<ResponseOutputMessage>(Assert.Single(finish.Response.Output)).Id);
    }

    [Fact]
    public void Provider_tool_completed_snapshot_matches_canonical_final_output_on_the_wire()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        _ = Event("tool-input-start", "run_1", new AIToolInputStartEventData
        {
            ToolName = "agent_run", ProviderExecuted = true
        }).ToResponseStreamPart(state);
        _ = Event("tool-input-available", "run_1", new AIToolInputAvailableEventData
        {
            ToolName = "agent_run", ProviderExecuted = true, Input = new { query = "news" }
        }).ToResponseStreamPart(state);
        var done = Assert.IsType<ResponseOutputItemDone>(Event("tool-output-available", "run_1", new AIToolOutputAvailableEventData
        {
            ToolName = "agent_run", ProviderExecuted = true, Output = new { final_field = true, answer = "Answer" }
        }).ToResponseStreamPart(state));
        var snapshot = JsonSerializer.SerializeToElement(done.Item, ResponseJson.Default);
        var finish = Assert.IsType<ResponseCompleted>(Event("finish", "resp_1",
            new AIFinishEventData { FinishReason = "stop", Model = "test" }).ToResponseStreamPart(state));
        var finalOutput = Assert.IsType<ResponseOutputItem<ResponseCustomToolCallItem>>(Assert.Single(finish.Response.Output));
        var finalJson = JsonSerializer.SerializeToElement(finalOutput, ResponseJson.Default);
        Assert.True(JsonElement.DeepEquals(snapshot, finalJson), "Completed items and final outputs must have the same canonical wire JSON.");
        Assert.True(JsonElement.DeepEquals(snapshot, JsonSerializer.SerializeToElement(done.Item, ResponseJson.Default)),
            "Emitted completed items must remain immutable.");
        Assert.True(finalJson.GetProperty("output").GetProperty("final_field").GetBoolean());
    }

    private static AIResponse Response(string status, AIOutputItem item) => new()
    {
        ProviderId = "test", Model = "test-model", Status = status,
        Output = new AIOutput { Items = [item] }
    };

    private static AIStreamEvent Event(string type, string? id, object data) => new()
    {
        ProviderId = "test", Event = new AIEventEnvelope { Type = type, Id = id, Data = data }
    };

    private static void AssertCanonical(ResponseResult response)
    {
        foreach (var value in response.Output)
        {
            Assert.IsAssignableFrom<ResponseOutputItem>(value);
            var json = JsonSerializer.SerializeToElement(value, ResponseJson.Default);
            Assert.NotNull(json.Deserialize<ResponseOutputItem>(ResponseJson.Default));
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("id").GetString()));
        }
        var copy = JsonSerializer.Deserialize<ResponseResult>(JsonSerializer.Serialize(response, ResponseJson.Default), ResponseJson.Default)!;
        Assert.Equal(response.Output.Count(), copy.Output.Count());
    }
}
