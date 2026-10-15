using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    // The generic chat mapper only projects native chat chunks, not typed Unified events.
    private static ChatCompletionUpdate ToDustChatUpdate(AIStreamEvent part, string model, string id,
        Dictionary<string, int> tools, HashSet<string> inputs)
    {
        var delta = new Dictionary<string, object?>();
        string? finish = null;
        var callId = part.Event.Id ?? "";
        Dictionary<string, object?> Tool(string? name = null)
        {
            var first = !tools.TryGetValue(callId, out var index);
            if (first) tools[callId] = index = tools.Count;
            var tool = new Dictionary<string, object?> { ["index"] = index, ["provider_executed"] = true };
            if (first)
            {
                tool["id"] = callId;
                tool["type"] = "function";
            }
            var function = new Dictionary<string, object?>();
            if (first && name is not null) function["name"] = name;
            tool["function"] = function;
            delta["tool_calls"] = new[] { tool };
            return function;
        }
        switch (part.Event.Data)
        {
            case AITextDeltaEventData text:
                delta["content"] = text.Delta;
                break;
            case AIReasoningDeltaEventData reasoning:
                delta["reasoning_content"] = reasoning.Delta;
                break;
            case AIToolInputStartEventData start:
                Tool(start.ToolName)["arguments"] = "";
                break;
            case AIToolInputAvailableEventData input when inputs.Add(callId):
                Tool(input.ToolName)["arguments"] = JsonSerializer.Serialize(input.Input, Json);
                break;
            case AIToolOutputAvailableEventData output:
                Tool(output.ToolName)["output"] = output.Output;
                break;
            case AIToolOutputErrorEventData error:
                Tool()["output"] = new { error = error.ErrorText };
                break;
            case AIFinishEventData completed:
                finish = completed.FinishReason == "tool-calls" ? "tool_calls" : completed.FinishReason;
                break;
        }
        var additional = new Dictionary<string, JsonElement>
        {
            ["dust_event"] = JsonSerializer.SerializeToElement(part.Event, Json)
        };
        if (part.Metadata?.TryGetValue("dust", out var metadata) == true)
            additional["dust"] = JsonSerializer.SerializeToElement(metadata, Json);
        return new ChatCompletionUpdate
        {
            Id = id, Model = model, Created = (part.Event.Timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            Choices = delta.Count > 0 || finish is not null
                ? [new Dictionary<string, object?> { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }]
                : [],
            AdditionalProperties = additional
        };
    }
}
