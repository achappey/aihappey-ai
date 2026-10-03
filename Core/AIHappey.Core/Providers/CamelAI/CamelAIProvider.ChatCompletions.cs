using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.CamelAI;

public sealed partial class CamelAIProvider
{
    // Keep this adapter provider-local: the generic mapper primarily round-trips native OpenAI chunks.
    private static ChatCompletionUpdate ChatChunk(AIStreamEvent evt, string id, string model, Dictionary<string, int> tools)
    {
        var delta = new Dictionary<string, object?>();
        string? finish = null;
        object? usage = null;
        switch (evt.Event.Data)
        {
            case AITextStartEventData: delta["role"] = "assistant"; break;
            case AITextDeltaEventData text: delta["content"] = text.Delta; break;
            case AIReasoningDeltaEventData reasoning: delta["reasoning_content"] = reasoning.Delta; break;
            case AIToolInputAvailableEventData call:
                var toolId = evt.Event.Id!;
                if (!tools.TryGetValue(toolId, out var index)) tools[toolId] = index = tools.Count;
                delta["tool_calls"] = new[] { new { index, id = toolId, type = "function", provider_executed = true,
                    function = new { name = call.ToolName, arguments = JsonSerializer.Serialize(call.Input, Json) } } };
                break;
            case AIToolOutputAvailableEventData output: delta["camelai_tool_result"] = new { id = evt.Event.Id, output = output.Output }; break;
            case AIToolOutputErrorEventData error: delta["camelai_tool_error"] = error; break;
            case AIFileEventData file: delta["camelai_file"] = file; break;
            case AIDataEventData data: delta["camelai_data"] = new { type = evt.Event.Type, data = data.Data }; break;
            case AIErrorEventData error: delta["camelai_error"] = error.ErrorText; break;
            case AIFinishEventData end:
                finish = end.FinishReason;
                usage = new { prompt_tokens = end.InputTokens, completion_tokens = end.OutputTokens, total_tokens = end.TotalTokens };
                break;
        }
        return new ChatCompletionUpdate
        {
            Id = id, Model = model, Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Choices = delta.Count == 0 && finish is null ? [] : [new { index = 0, delta, finish_reason = finish }], Usage = usage,
            AdditionalProperties = new() { ["camelai"] = Serialize(evt.Metadata?.GetValueOrDefault("camelai")) }
        };
    }
}
