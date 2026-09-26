using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Connect0;

public sealed partial class Connect0Provider
{
    private static void AttachChatMetadata(ChatCompletion completion, AIResponse response)
    {
        if (response.Metadata?.TryGetValue("connect0", out var scoped) != true || scoped is null) return;
        var value = Element(scoped);
        completion.AdditionalProperties ??= new Dictionary<string, JsonElement>();
        completion.AdditionalProperties["connect0"] = value;
        if (String(value, "run_id") is { Length: > 0 } id) completion.Id = id;
    }

    private static ResponseResult ToConnect0ResponseResult(AIResponse response)
    {
        var mapped = response.ToResponseResult();
        if (response.Metadata?.TryGetValue("connect0", out var scoped) == true && scoped is not null
            && String(Element(scoped), "run_id") is { Length: > 0 } id)
            mapped.Id = id;
        var output = mapped.Output.ToList();
        foreach (var tool in response.Output?.Items?.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>() ?? [])
        {
            var identity = tool.Output is not null ? Element(tool.Output) : default;
            var content = Property(identity, "structuredContent") ?? identity;
            output.Add(new
            {
                type = "function_call", id = tool.ToolCallId, call_id = tool.ToolCallId,
                name = tool.ToolName, arguments = JsonSerializer.Serialize(tool.Input, Json),
                status = "completed", provider_executed = true
            });
            output.Add(new
            {
                type = "function_call_output", id = tool.ToolCallId + "-output", call_id = tool.ToolCallId,
                output = content.ValueKind == JsonValueKind.Undefined ? "{}" : content.GetRawText(),
                status = "completed", provider_executed = true
            });
        }
        mapped.Output = output;
        return mapped;
    }

    private static ChatCompletionUpdate ToChatUpdate(AIStreamEvent evt, string model)
    {
        var scoped = evt.Metadata?.TryGetValue("connect0", out var value) == true && value is not null
            ? Element(value) : JsonSerializer.SerializeToElement(new { }, Json);
        var additional = new Dictionary<string, JsonElement> { ["connect0"] = scoped };
        if (evt.Event.Type == "data-connect0-run-event")
            additional["connect0_event"] = Element(evt.Event.Data);
        var id = String(scoped, "run_id") ?? evt.Event.Id ?? $"connect0_{Guid.NewGuid():N}";
        IEnumerable<object> choices = evt.Event.Type switch
        {
            "text-start" => [new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }],
            "text-delta" when evt.Event.Data is AITextDeltaEventData delta
                => [new { index = 0, delta = new { content = delta.Delta }, finish_reason = (string?)null }],
            "finish" when evt.Event.Data is AIFinishEventData finish
                => [new { index = 0, delta = new { }, finish_reason = finish.FinishReason }],
            _ => []
        };
        return new ChatCompletionUpdate
        {
            Id = id, Model = model, Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Choices = choices, AdditionalProperties = additional
        };
    }
}
