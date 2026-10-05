using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tembo;

public partial class TemboProvider
{
    // The shared single-event reverse mapper only handles native Chat Completions JSON.
    // This adapter changes protocol shape only; submission and output stay unified.
    private static ChatCompletionUpdate? ToTemboChatUpdate(AIStreamEvent part, string model, TemboChatStreamState state)
    {
        var delta = new Dictionary<string, object?>();
        string? finishReason = null;
        var additional = new Dictionary<string, JsonElement>();
        switch (part.Event.Type)
        {
            case "text-start":
                if (state.RoleEmitted) return null;
                delta["role"] = "assistant";
                state.RoleEmitted = true;
                break;
            case "text-delta" when part.Event.Data is AITextDeltaEventData text:
                // Persisted messages are separate unified text blocks, but Chat Completions has one content string.
                delta["content"] = (state.TextBlocks.Add(part.Event.Id ?? "text") && state.TextBlocks.Count > 1 ? "\n\n" : "") + text.Delta;
                break;
            case "tool-input-available" when part.Event.Data is AIToolInputAvailableEventData tool:
                var id = part.Event.Id!;
                state.ToolIndices.TryAdd(id, state.ToolIndices.Count);
                delta["tool_calls"] = new[] { new
                {
                    index = state.ToolIndices[id], id, type = "function",
                    function = new { name = tool.ToolName, arguments = JsonSerializer.Serialize(tool.Input, TemboJson) },
                    provider_executed = true, provider_metadata = tool.ProviderMetadata
                } };
                break;
            case "tool-output-available" when part.Event.Data is AIToolOutputAvailableEventData result:
                delta["tool_responses"] = new[] { new
                {
                    id = part.Event.Id, name = result.ToolName, output = result.Output,
                    provider_executed = true, preliminary = result.Preliminary
                } };
                break;
            case "source-url" when part.Event.Data is AISourceUrlEventData source:
                delta["sources"] = new[] { new { url = source.Url, title = source.Title } };
                break;
            case "data-tembo-session-event" when part.Event.Data is AIDataEventData data:
                additional["tembo_event"] = JsonSerializer.SerializeToElement(data.Data, TemboJson);
                break;
            case "finish" when part.Event.Data is AIFinishEventData finish:
                finishReason = finish.FinishReason ?? "stop";
                break;
            default: return null;
        }
        if (!state.RoleEmitted)
        {
            delta["role"] = "assistant";
            state.RoleEmitted = true;
        }
        if (part.Metadata is not null)
            additional["provider_metadata"] = JsonSerializer.SerializeToElement(part.Metadata, TemboJson);
        var choice = new Dictionary<string, object?> { ["index"] = 0, ["delta"] = delta };
        if (finishReason is not null) choice["finish_reason"] = finishReason;
        return new ChatCompletionUpdate
        {
            Id = state.Id, Object = "chat.completion.chunk", Created = state.Created,
            Model = model.StartsWith("tembo/", StringComparison.Ordinal) ? model : "tembo/" + model,
            Choices = [choice], AdditionalProperties = additional
        };
    }

    private sealed class TemboChatStreamState
    {
        public string Id { get; } = "chatcmpl_" + Guid.NewGuid().ToString("N");
        public long Created { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public bool RoleEmitted { get; set; }
        public HashSet<string> TextBlocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ToolIndices { get; } = new(StringComparer.Ordinal);
    }
}
