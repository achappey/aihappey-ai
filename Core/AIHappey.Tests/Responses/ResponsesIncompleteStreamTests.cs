using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesIncompleteStreamTests
{
    private const string ProviderId = "terminal-test";

    [Theory]
    [InlineData("completed", "stop", false)]
    [InlineData("incomplete", "other", false)]
    [InlineData("incomplete", "other", true)]
    public void Terminal_response_emits_one_finish_with_usage_and_provider_metadata(
        string status, string finishReason, bool unknownEnvelope)
    {
        var part = Parse(TerminalJson(status), unknownEnvelope);
        var events = part.ToUnifiedStreamEvent(ProviderId).ToList();

        var streamEvent = Assert.Single(events);
        Assert.Equal("finish", streamEvent.Event.Type);
        Assert.Equal($"response.{status}", streamEvent.Metadata!["responses.type"]);
        var finish = Assert.IsType<AIFinishEventData>(streamEvent.Event.Data);
        Assert.Equal(17, finish.SequenceNumber);
        Assert.Equal("test-model", finish.Model);
        Assert.Equal(1792660001L, finish.CompletedAt);
        Assert.Equal(finishReason, finish.FinishReason);
        Assert.Equal(100, finish.InputTokens);
        Assert.Equal(20, finish.OutputTokens);
        Assert.Equal(120, finish.TotalTokens);
        Assert.Equal(0.012m, finish.MessageMetadata!.Gateway!.Cost);
        var response = Assert.IsType<ResponseResult>(finish.Response);
        Assert.Equal(status, response.Status);
        Assert.True(response.AdditionalProperties!["future"].GetProperty("preserved").GetBoolean());
        if (status == "incomplete")
            Assert.Equal("max_output_tokens", response.IncompleteDetails!.Value.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_response_mapping_does_not_mutate_or_retype_the_native_event(bool unknownEnvelope)
    {
        var part = Parse(TerminalJson("incomplete"), unknownEnvelope);
        if (unknownEnvelope)
            Assert.IsType<ResponseUnknownEvent>(part);
        else
            Assert.IsType<ResponseIncompleteEvent>(part);
        var before = JsonSerializer.Serialize<ResponseStreamPart>(part, ResponseJson.Default);

        Assert.Single(part.ToUnifiedStreamEvent(ProviderId));

        Assert.Equal("response.incomplete", part.Type);
        Assert.Equal(before, JsonSerializer.Serialize<ResponseStreamPart>(part, ResponseJson.Default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_response_without_usage_does_not_invent_token_counts(bool unknownEnvelope)
    {
        var part = Parse("""
            {"type":"response.incomplete","response":{"id":"r","model":"test-model","status":"incomplete","output":[]}}
            """, unknownEnvelope);

        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(part.ToUnifiedStreamEvent(ProviderId)).Event.Data);

        Assert.Null(finish.InputTokens);
        Assert.Null(finish.OutputTokens);
        Assert.Null(finish.TotalTokens);
        Assert.Null(finish.MessageMetadata!.Gateway);
        Assert.Null(Assert.IsType<ResponseResult>(finish.Response).Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_terminal_response_clears_pending_shell_stream_state(bool unknownEnvelope)
    {
        var state = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        Parse("""
            {"type":"response.created","response":{"id":"r","model":"test-model","status":"in_progress","output":[]}}
            """).ToUnifiedStreamEvent(ProviderId, state).ToList();
        Parse("""
            {"type":"response.output_item.added","output_index":0,"item":{"id":"shell_terminal_test","type":"shell_call","status":"in_progress","call_id":"call_terminal_test","action":{"type":"exec","commands":[]}}}
            """).ToUnifiedStreamEvent(ProviderId, state).ToList();
        var staleCommand = Parse("""
            {"type":"response.shell_call_command.added","output_index":0,"command_index":0,"command":"echo stale"}
            """);

        try
        {
            Assert.Single(Parse(TerminalJson("incomplete"), unknownEnvelope).ToUnifiedStreamEvent(ProviderId, state));
            Assert.Empty(staleCommand.ToUnifiedStreamEvent(ProviderId, state));
        }
        finally
        {
            Parse(TerminalJson("completed")).ToUnifiedStreamEvent(ProviderId, state).ToList();
        }
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("incomplete", false)]
    [InlineData("incomplete", true)]
    public void Disposing_at_terminal_finish_clears_pending_shell_stream_state(string status, bool unknownEnvelope)
    {
        var state = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        Parse("""
            {"type":"response.created","response":{"id":"r","model":"test-model","status":"in_progress","output":[]}}
            """).ToUnifiedStreamEvent(ProviderId, state).ToList();
        Parse("""
            {"type":"response.output_item.added","output_index":0,"item":{"id":"shell_disposal_test","type":"shell_call","status":"in_progress","call_id":"call_disposal_test","action":{"type":"exec","commands":[]}}}
            """).ToUnifiedStreamEvent(ProviderId, state).ToList();

        try
        {
            using (var enumerator = Parse(TerminalJson(status), unknownEnvelope).ToUnifiedStreamEvent(ProviderId, state).GetEnumerator())
            {
                Assert.True(enumerator.MoveNext());
                Assert.Equal("finish", enumerator.Current.Event.Type);
                // A consumer can stop at finish without advancing the iterator again.
            }

            var staleCommand = Parse("""
                {"type":"response.shell_call_command.added","output_index":0,"command_index":0,"command":"echo stale"}
                """);
            Assert.Empty(staleCommand.ToUnifiedStreamEvent(ProviderId, state));
        }
        finally
        {
            Parse(TerminalJson("completed")).ToUnifiedStreamEvent(ProviderId, state).ToList();
        }
    }

    [Fact]
    public void Failed_response_still_emits_an_error_instead_of_a_successful_finish()
    {
        var part = Parse("""
            {"type":"response.failed","response":{"id":"r","model":"test-model","status":"failed","output":[],"error":{"code":"bad_request","message":"Provider failure"}}}
            """);

        var streamEvent = Assert.Single(part.ToUnifiedStreamEvent(ProviderId));

        Assert.Equal("error", streamEvent.Event.Type);
        Assert.Equal("Provider failure", Assert.IsType<AIErrorEventData>(streamEvent.Event.Data).ErrorText);
    }

    [Theory]
    [InlineData("""{"type":"response.incomplete"}""", false)]
    [InlineData("""{"type":"response.incomplete"}""", true)]
    [InlineData("""{"type":"response.incomplete","response":null}""", false)]
    [InlineData("""{"type":"response.incomplete","response":null}""", true)]
    public void Incomplete_event_without_a_response_object_does_not_invent_a_finish(string json, bool unknownEnvelope)
    {
        var part = Parse(json, unknownEnvelope);

        Assert.Empty(part.ToUnifiedStreamEvent(ProviderId));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"not a response\"")]
    public void Malformed_known_incomplete_response_payload_is_rejected_during_parsing(string response)
    {
        Assert.Throws<JsonException>(() => Parse("{\"type\":\"response.incomplete\",\"response\":" + response + "}"));
    }

    [Fact]
    public void Unrelated_unknown_event_is_not_treated_as_terminal()
    {
        var part = Parse(TerminalJson("incomplete").Replace("response.incomplete", "response.future_event"));

        Assert.Empty(part.ToUnifiedStreamEvent(ProviderId));
    }

    private static string TerminalJson(string status) => """
        {"type":"response.STATUS","sequence_number":17,"response":{
          "id":"r","model":"test-model","status":"STATUS","created_at":1792660000,"completed_at":1792660001,
          "output":[],"usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120},
          "metadata":{"gateway":{"cost":0.012}},"incomplete_details":{"reason":"max_output_tokens"},
          "future":{"preserved":true}}}
        """.Replace("STATUS", status);

    private static ResponseStreamPart Parse(string json, bool unknownEnvelope = false)
    {
        if (!unknownEnvelope)
            return JsonSerializer.Deserialize<ResponseStreamPart>(json, ResponseJson.Default)!;

        // Construct the provider-supplied envelope explicitly: the converter now
        // recognizes response.incomplete as a dedicated native event type.
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ResponseUnknownEvent
        {
            Type = root.GetProperty("type").GetString()!,
            SequenceNumber = root.TryGetProperty("sequence_number", out var sequenceNumber)
                ? sequenceNumber.GetInt32() : null,
            Data = root.EnumerateObject()
                .Where(property => property.Name is not ("type" or "sequence_number"))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
    }
}
