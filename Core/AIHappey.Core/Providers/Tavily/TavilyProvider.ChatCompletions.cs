using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    // The shared reverse streaming mapper currently handles native JSON chunks only.
    // Keep this small typed-event adapter local; execution stays entirely unified.
    private static ChatCompletionUpdate? ToChatCompletionUpdate(AIStreamEvent evt, string model,
        TavilyChatCompletionStreamingState state)
    {
        var delta = new Dictionary<string, object?>();
        object? usage = null;
        string? finishReason = null;
        switch (evt.Event.Type)
        {
            case "text-start":
                if (state.RoleEmitted) return null;
                state.RoleEmitted = true;
                delta["role"] = "assistant";
                break;
            case "text-delta" when evt.Event.Data is AITextDeltaEventData text:
                delta["content"] = text.Delta;
                if (!state.RoleEmitted) delta["role"] = "assistant";
                state.RoleEmitted = true;
                break;
            case "source-url" when evt.Event.Data is AISourceUrlEventData source:
                delta["sources"] = new[] { new { url = source.Url, title = source.Title, provider_metadata = source.ProviderMetadata } };
                break;
            case "tool-input-available" when evt.Event.Data is AIToolInputAvailableEventData tool:
                var toolId = evt.Event.Id!;
                state.ToolIndices.TryAdd(toolId, state.ToolIndices.Count);
                delta["tool_calls"] = new[] { new
                {
                    index = state.ToolIndices[toolId], id = toolId, type = "function",
                    function = new { name = tool.ToolName, arguments = JsonSerializer.Serialize(tool.Input, JsonSerializerOptions.Web) },
                    provider_executed = tool.ProviderExecuted, provider_metadata = tool.ProviderMetadata
                } };
                break;
            case "tool-output-available" when evt.Event.Data is AIToolOutputAvailableEventData output:
                delta["tool_responses"] = new[] { new
                { id = evt.Event.Id, name = output.ToolName, output = output.Output, provider_executed = output.ProviderExecuted } };
                break;
            case "finish" when evt.Event.Data is AIFinishEventData finish:
                finishReason = finish.FinishReason ?? "stop";
                usage = finish.MessageMetadata?.Usage;
                break;
            default:
                return null;
        }
        var choice = new Dictionary<string, object?> { ["index"] = 0, ["delta"] = delta };
        if (finishReason is not null) choice["finish_reason"] = finishReason;
        return new ChatCompletionUpdate
        {
            Id = state.Id, Model = model, Created = state.Created, Choices = [choice], Usage = usage,
            AdditionalProperties = new Dictionary<string, JsonElement>
            { ["provider_metadata"] = JsonSerializer.SerializeToElement(TextProviderMetadata(evt.Metadata ?? []), JsonSerializerOptions.Web) }
        };
    }

    private sealed class TavilyChatCompletionStreamingState
    {
        public string Id { get; } = $"chatcmpl_{Guid.NewGuid():N}";
        public long Created { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public bool RoleEmitted { get; set; }
        public Dictionary<string, int> ToolIndices { get; } = new(StringComparer.Ordinal);
    }
}
