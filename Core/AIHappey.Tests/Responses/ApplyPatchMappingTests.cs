using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Responses;

public sealed class ApplyPatchMappingTests
{
    private const string NativeCall = """
        {"type":"apply_patch_call","id":"apc_item","call_id":"call_patch","status":"completed",
         "operation":{"type":"update_file","path":"test.txt","diff":"@@\n-old\n+new"},
         "caller":{"type":"program","caller_id":"program_1"},"created_by":"agent_1"}
        """;

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public void Native_request_call_and_output_survive_repeated_replay(string status)
    {
        var json = "{\"model\":\"openai/test\",\"input\":[" + NativeCall
            + ",{\"type\":\"apply_patch_call_output\",\"call_id\":\"call_patch\",\"status\":\"" + status + "\",\"output\":\"Result\"}]}";
        var request = JsonSerializer.Deserialize<ResponseRequest>(json, ResponseJson.Default)!;
        for (var turn = 0; turn < 3; turn++)
        {
            request = request.ToUnifiedRequest("openai").ToResponseRequest("openai");
            var items = request.Input!.Items!;
            Assert.Equal(2, items.Count);
            var call = Assert.IsType<ResponseApplyPatchCallItem>(items[0]);
            Assert.Equal("apc_item", call.Id); Assert.Equal("call_patch", call.CallId);
            Assert.Equal("program_1", call.Caller?.CallerId);
            Assert.Equal("@@\n-old\n+new", Assert.IsType<ResponseApplyPatchUpdateFile>(call.Operation).Diff);
            var output = Assert.IsType<ResponseApplyPatchCallOutputItem>(items[1]);
            Assert.Equal(status, output.Status); Assert.Equal("Result", output.Output);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Client_history_replays_native_patch_results_not_function_results(bool failed)
    {
        var json = """
            {"model":"openai/test","messages":[{"role":"assistant","parts":[
              {"type":"tool-apply_patch","toolCallId":"call_patch","state":"output-available","providerExecuted":false,
               "callProviderMetadata":{"openai":{"type":"apply_patch_call","id":"apc_item","call_id":"call_patch","status":"completed"}},
               "input":{"operation":{"type":"create_file","path":"test.txt","diff":"+hello"}},
               "output":{"isError":false,"content":[{"type":"text","text":"Created"}],"structuredContent":{"status":"completed","output":"Created"}}}
            ]}]}
            """;
        if (failed) json = json.Replace("\"isError\":false", "\"isError\":true")
            .Replace("\"status\":\"completed\",\"output\":\"Created\"", "\"status\":\"failed\",\"output\":\"Conflict\"");
        var chat = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)!;
        var unified = chat.ToUnifiedRequest("openai");
        var request = unified.ToResponseRequest("openai");
        Assert.Collection(request.Input!.Items!,
            item => Assert.IsType<ResponseApplyPatchCallItem>(item),
            item => Assert.Equal(failed ? "failed" : "completed", Assert.IsType<ResponseApplyPatchCallOutputItem>(item).Status));
    }

    [Fact]
    public void Streaming_waits_for_complete_operation_deduplicates_and_reverses_natively()
    {
        var call = JsonSerializer.Deserialize<ResponseStreamItem>(NativeCall, ResponseJson.Default)!;
        var state = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        Assert.Empty(new ResponseOutputItemAdded { Item = call }.ToUnifiedStreamEvent("openai", state));
        var done = new ResponseOutputItemDone { Item = call };
        var events = done.ToUnifiedStreamEvent("openai", state).ToList();
        Assert.Equal(2, events.Count);
        var available = Assert.IsType<AIToolInputAvailableEventData>(events[1].Event.Data);
        Assert.False(available.ProviderExecuted); Assert.Equal("apply_patch", available.ToolName);
        Assert.Equal("call_patch", events[1].Event.Id);
        Assert.Empty(done.ToUnifiedStreamEvent("openai", state));
        var reverse = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        var added = Assert.IsType<ResponseOutputItemAdded>(events[0].ToResponseStreamPart(reverse));
        var finished = Assert.IsType<ResponseOutputItemDone>(events[1].ToResponseStreamPart(reverse));
        Assert.Equal("apply_patch_call", added.Item.Type); Assert.Equal("in_progress", added.Item.Status);
        Assert.Equal("apply_patch_call", finished.Item.Type); Assert.Equal("apc_item", finished.Item.Id);
        Assert.Equal("call_patch", finished.Item.CallId);
        Assert.IsType<ResponseApplyPatchUpdateFile>(finished.Item.Operation);
    }

    [Fact]
    public void Nonstream_response_roundtrip_preserves_native_call_and_requests_continuation()
    {
        var response = JsonSerializer.Deserialize<ResponseResult>("{\"id\":\"resp_patch\",\"model\":\"test\",\"status\":\"completed\",\"output\":[" + NativeCall + "]}", ResponseJson.Default)!;
        var unified = response.ToUnifiedResponse("openai");
        var part = unified.Output!.Items!.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>().Single();
        Assert.False(part.ProviderExecuted); Assert.Equal("apply_patch", part.ToolName);
        var roundtrip = unified.ToResponseResult();
        var call = Assert.IsType<ResponseApplyPatchCallItem>(Assert.Single(roundtrip.Output));
        Assert.Equal("apc_item", call.Id); Assert.Equal("call_patch", call.CallId);
        var finish = new ResponseCompleted { Response = response }.ToUnifiedStreamEvent("openai")
            .Select(evt => evt.Event.Data).OfType<AIFinishEventData>().Single();
        Assert.Equal("tool-calls", finish.FinishReason);
    }

    [Fact]
    public void Denied_history_emits_failed_native_result_without_releasing_original_output()
    {
        var request = new AIRequest
        {
            ProviderId = "openai", Model = "test",
            Input = new AIInput { Items = [new AIInputItem { Type = "message", Role = "assistant", Content = [new AIToolCallContentPart
            {
                Type = "apply_patch_call", ToolCallId = "call_patch", ToolName = "apply_patch", ProviderExecuted = false,
                State = "approval-responded", Approval = new AIToolCallApproval { Approved = false, Reason = "Denied result" },
                Input = new { operation = new ResponseApplyPatchDeleteFile { Path = "test.txt" } },
                Output = new { status = "completed", output = "private output" }
            }] }] }
        };
        var result = Assert.IsType<ResponseApplyPatchCallOutputItem>(request.ToResponseRequest("openai").Input!.Items![1]);
        Assert.Equal("failed", result.Status); Assert.Equal("Denied result", result.Output);
    }
}
