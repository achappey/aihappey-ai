using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsCurrentContractMappingTests
{
    [Theory]
    [InlineData("{\"answer\":[1,true,null]}")]
    [InlineData("\"plain result\"")]
    [InlineData("[{\"type\":\"text\",\"text\":\"done\"}]")]
    public void Native_request_steps_preserve_payloads_and_extensions_without_raw_input_shortcut(string result)
    {
        var native = JsonSerializer.Deserialize<InteractionRequest>($$$"""
            {"input":[
              {"type":"thought","signature":"opaque","summary":[{"type":"text","text":"Checking."}],"future_thought":{"keep":true}},
              {"type":"function_call","id":"call-a","name":"lookup","arguments":{"nested":[1,true,null]},"signature":"call-signature","future_call":{"keep":1}},
              {"type":"function_result","call_id":"call-a","name":"lookup","result":{{{result}}},"is_error":false,"future_result":[2,3]}
            ]}
            """, InteractionJson.Default)!;
        var unified = native.ToUnifiedRequest("google");
        var reasoning = Assert.IsType<AIReasoningContentPart>(unified.Input!.Items![0].Content![0]);
        Assert.Equal("Checking.", reasoning.Text);
        Assert.Equal("opaque", reasoning.Signature);

        var replay = new AIRequest { ProviderId = "google", Input = new AIInput { Items = unified.Input.Items } }
            .ToInteractionRequest("google");

        Assert.Equal(new[] { "thought", "function_call", "function_result" }, replay.Input!.Steps!.Select(s => s.Type));
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Input, InteractionJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, InteractionJson.Default)));
    }

    [Fact]
    public void Response_mapping_keeps_thoughts_and_tools_outside_model_content()
    {
        var native = JsonSerializer.Deserialize<Interaction>("""
            {"status":"future-paused","steps":[
              {"type":"thought","signature":"private","summary":[{"type":"text","text":"Thinking."}]},
              {"type":"function_call","id":"call-1","name":"lookup","arguments":{"q":"test"}},
              {"type":"function_result","call_id":"call-1","name":"lookup","result":{"ok":true}},
              {"type":"model_output","content":[{"type":"text","text":"Done."}]}
            ]}
            """, InteractionJson.Default)!;
        var unified = native.ToUnifiedResponse("google");
        Assert.Equal("future-paused", unified.Status);
        Assert.Equal(new[] { "thought", "function_call", "function_result", "message" }, unified.Output!.Items!.Select(i => i.Type));

        var replay = unified.ToInteraction();
        Assert.Equal(native.Steps!.Select(s => s.Type), replay.Steps!.Select(s => s.Type));
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Steps, InteractionJson.Default),
            JsonSerializer.SerializeToElement(replay.Steps, InteractionJson.Default)));
    }

    [Fact]
    public void Typed_stream_deltas_preserve_tool_correlation_thought_signature_and_event_ids()
    {
        InteractionStreamEventPart[] parts =
        [
            new InteractionStepStartEvent { Index = 2, EventId = "thought-start", Step = new InteractionThoughtStep() },
            new InteractionStepDeltaEvent { Index = 2, EventId = "summary", Delta = new InteractionThoughtSummaryDeltaData { Content = new InteractionTextContent { Text = "Checking." } } },
            new InteractionStepDeltaEvent { Index = 2, EventId = "signature", Delta = new InteractionThoughtSignatureDeltaData { Signature = "opaque" } },
            new InteractionStepStopEvent { Index = 2, EventId = "thought-stop" },
            new InteractionStepStartEvent { Index = 7, EventId = "call-start", Step = new InteractionFunctionCallStep { Id = "call-7", Name = "lookup" } },
            new InteractionStepDeltaEvent { Index = 7, EventId = "args-a", Delta = new InteractionArgumentsDeltaData { Arguments = "{\"q\":" } },
            new InteractionStepDeltaEvent { Index = 7, EventId = "args-b", Delta = new InteractionArgumentsDeltaData { Arguments = "\"test\"}" } },
            new InteractionStepStopEvent { Index = 7, EventId = "call-stop" }
        ];
        var events = parts.ToUnifiedStreamEvents("google").ToList();
        var thought = events.Single(e => e.Event.Type == "reasoning-end");
        Assert.Equal("opaque", Assert.IsType<AIReasoningEndEventData>(thought.Event.Data).Signature);
        Assert.Equal("thought-stop", thought.Metadata!["interactions.event_id"]);
        var call = events.Single(e => e.Event.Type == "tool-input-available");
        Assert.Equal("call-7", call.Event.Id);
        Assert.Equal("call-stop", call.Metadata!["interactions.event_id"]);
        var data = Assert.IsType<AIToolInputAvailableEventData>(call.Event.Data);
        Assert.False(data.ProviderExecuted);
        Assert.Equal("lookup", data.ToolName);
        Assert.Equal("test", JsonSerializer.SerializeToElement(data.Input).GetProperty("q").GetString());
        Assert.All(events.Where(e => e.Event.Type == "tool-input-delta"), e => Assert.Equal("call-7", e.Event.Id));
    }

    [Fact]
    public void Reverse_stream_mapping_emits_valid_step_envelopes_and_json_arguments()
    {
        var text = new AIStreamEvent { ProviderId = "google", Event = new AIEventEnvelope { Type = "text-start" } };
        var start = Assert.IsType<InteractionStepStartEvent>(text.ToInteractionStreamEvent());
        Assert.IsType<InteractionTextContent>(Assert.Single(Assert.IsType<InteractionModelOutputStep>(start.Step).Content!));

        var tool = new AIStreamEvent
        {
            ProviderId = "google",
            Event = new AIEventEnvelope
            {
                Type = "tool-input-available", Id = "call-1",
                Data = new AIToolInputAvailableEventData { ToolName = "lookup", ProviderExecuted = false, Input = new { q = new[] { 1, 2 } } }
            }
        };
        var call = Assert.IsType<InteractionFunctionCallStep>(Assert.IsType<InteractionStepStartEvent>(tool.ToInteractionStreamEvent()).Step);
        Assert.Equal("call-1", call.Id);
        Assert.Equal(2, call.Arguments!.Value.GetProperty("q").GetArrayLength());
    }

    [Fact]
    public void Absent_status_stays_null_in_response_and_stream_finish_mapping()
    {
        Assert.Null(new AIResponse { ProviderId = "google" }.ToInteraction().Status);
        var finish = new AIStreamEvent
        {
            ProviderId = "google",
            Event = new AIEventEnvelope { Type = "finish", Data = new AIFinishEventData { Model = "test" } }
        };
        Assert.Null(Assert.IsType<InteractionCompletedEvent>(finish.ToInteractionStreamEvent()).Interaction!.Status);
    }

    [Fact]
    public void Audio_sample_rate_survives_content_replay_and_typed_stream_deltas()
    {
        var native = new InteractionRequest
        {
            Input = new InteractionsInput(new InteractionAudioContent { Data = "YQ==", MimeType = "audio/pcm", SampleRate = 24000, Channels = 1 })
        };
        var replay = native.ToUnifiedRequest("google").ToInteractionRequest("google");
        var audio = Assert.IsType<InteractionAudioContent>(Assert.Single(Assert.IsType<InteractionUserInputStep>(Assert.Single(replay.Input!.Steps!)).Content!));
        Assert.Equal(24000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);

        InteractionStreamEventPart[] parts =
        [
            new InteractionStepStartEvent { Index = 0, Step = new InteractionModelOutputStep() },
            new InteractionStepDeltaEvent { Index = 0, Delta = new InteractionAudioDeltaData { Data = "YQ==", MimeType = "audio/pcm", SampleRate = 24000, Channels = 1 } },
            new InteractionStepStopEvent { Index = 0 }
        ];
        var output = parts.ToUnifiedStreamEvents("google").Single(e => e.Event.Type == "tool-output-available");
        Assert.Equal(24000, Assert.IsType<AIToolOutputAvailableEventData>(output.Event.Data).ProviderMetadata!["google"]["interactions.rate"]);
    }

    [Fact]
    public void Typed_step_input_maps_roles_and_content_and_replays_current_steps()
    {
        var native = new InteractionRequest
        {
            Input = new InteractionsInput(new List<InteractionStep>
            {
                new InteractionUserInputStep { Content = [new InteractionTextContent { Text = "Question" }] },
                new InteractionModelOutputStep { Content = [new InteractionTextContent { Text = "Answer" }] }
            })
        };
        var unified = native.ToUnifiedRequest("google");
        Assert.NotNull(unified.Input?.Items);
        Assert.Equal(new[] { "user", "assistant" }, unified.Input.Items.Select(i => i.Role));
        Assert.Equal(new[] { "Question", "Answer" }, unified.Input.Items.Select(i => Assert.IsType<AITextContentPart>(Assert.Single(i.Content!)).Text));
        var replay = unified.ToInteractionRequest("google");
        Assert.Equal(new[] { "user_input", "model_output" }, replay.Input!.Steps!.Select(s => s.Type));
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Input, InteractionJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, InteractionJson.Default)));
    }
}
