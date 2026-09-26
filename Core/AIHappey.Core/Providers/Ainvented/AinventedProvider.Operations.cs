using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Ainvented;

public sealed partial class AinventedProvider
{
    private async Task<AIResponse> ExecuteOperationAsync(AIRequest request, string route, CancellationToken ct)
    {
        var options = Options(request);
        JsonElement raw;
        var toolName = route == "workflow" ? "execute_ainvented_workflow" : "run_ainvented_task";
        if (route == "workflow")
        {
            var payload = new JsonObject();
            foreach (var key in new[] { "input_vars", "images", "audios", "urls", "url_inputs", "external_apis", "model", "reasoning_effort", "project_id" })
                if (options.TryGetPropertyValue(key, out var value)) payload[key] = value?.DeepClone();
            raw = await SendJsonAsync(HttpMethod.Post, "workflows/execute", payload, ct);
        }
        else
        {
            var taskId = route[5..];
            if (string.IsNullOrWhiteSpace(taskId) || taskId.Contains('/')) throw new ArgumentException("Invalid Ainvented task id.");
            var sessionId = Option(options, "session_id") ?? FindSessionId(request);
            if (Option(options, "action") == "runs")
            {
                var path = $"tasks/{Uri.EscapeDataString(taskId)}/runs";
                if (!string.IsNullOrWhiteSpace(sessionId)) path += $"?session_id={Uri.EscapeDataString(sessionId)}";
                raw = await SendJsonAsync(HttpMethod.Get, path, null, ct);
                toolName = "list_ainvented_task_runs";
            }
            else
            {
                var payload = new JsonObject();
                if (!string.IsNullOrWhiteSpace(sessionId)) payload["session_id"] = sessionId;
                if (options.TryGetPropertyValue("project_id", out var project)) payload["project_id"] = project?.DeepClone();
                // Task runs accept the chat-completions message shape, not an abstract prompt.
                var chat = request.ToChatCompletionOptions(GetIdentifier());
                var messages = JsonSerializer.SerializeToNode(chat.Messages, Json);
                if (messages is JsonArray { Count: > 0 }) payload["messages"] = messages;
                raw = await SendJsonAsync(HttpMethod.Post, $"tasks/{Uri.EscapeDataString(taskId)}/run", payload, ct);
            }
        }

        var result = route == "workflow" ? Property(raw, "output") : Property(raw, "result") ?? Property(raw, "data");
        var text = result?.ValueKind == JsonValueKind.String ? result.Value.GetString() : result?.GetRawText();
        var metadata = Metadata(raw);
        var tool = new AIToolCallContentPart
        {
            Type = "tool-call", ToolName = toolName,
            ToolCallId = $"ainvented-{String(raw, "id") ?? Guid.NewGuid().ToString("N")}",
            Input = route == "workflow" ? new { workflow = true } : new { task_id = route[5..] },
            Output = new CallToolResult { StructuredContent = raw.Clone() },
            State = "output-available", ProviderExecuted = true, Metadata = metadata
        };
        var content = new List<AIContentPart> { tool };
        if (!string.IsNullOrEmpty(text)) content.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = route.ToModelId(GetIdentifier()),
            Status = String(raw, "status") ?? (Property(raw, "success") is { ValueKind: JsonValueKind.False } ? "failed" : "completed"),
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Type = "message", Content = content, Metadata = metadata }],
                Metadata = metadata
            },
            Usage = TaskUsage(raw), Metadata = metadata
        };
    }

    private static AIUsage? TaskUsage(JsonElement raw)
    {
        var prompt = Property(raw, "prompt_tokens");
        var completion = Property(raw, "completion_tokens");
        if (prompt is not { ValueKind: JsonValueKind.Number } && completion is not { ValueKind: JsonValueKind.Number }) return null;
        var input = prompt?.GetInt32() ?? 0;
        var output = completion?.GetInt32() ?? 0;
        return new AIUsage { InputTokens = input, OutputTokens = output, TotalTokens = input + output };
    }

    private static string? FindSessionId(AIRequest request)
    {
        if (request.Input?.Metadata?.TryGetValue("session_id", out var value) == true)
            return AsJson(value).ValueKind == JsonValueKind.String ? AsJson(value).GetString() : null;
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
        {
            foreach (var part in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (part.ProviderExecuted != true || part.Output is null) continue;
                var output = AsJson(part.Output);
                var body = Property(output, "structuredContent") ?? output;
                var session = String(body, "session_id") ?? String(body, "sessionId");
                if (!string.IsNullOrEmpty(session)) return session;
            }
        }
        return null;
    }

    private static IEnumerable<AIStreamEvent> OperationEvents(AIResponse response)
    {
        var content = response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [];
        foreach (var tool in content.OfType<AIToolCallContentPart>())
        {
            yield return Event("tool-input-available", tool.ToolCallId,
                new AIToolInputAvailableEventData { ToolName = tool.ToolName!, Input = tool.Input ?? new { }, ProviderExecuted = true }, response.Metadata);
            yield return Event("tool-output-available", tool.ToolCallId,
                new AIToolOutputAvailableEventData { ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true }, response.Metadata);
        }
        foreach (var text in content.OfType<AITextContentPart>())
        {
            var id = Guid.NewGuid().ToString("N");
            yield return Event("text-start", id, new AITextStartEventData(), response.Metadata);
            yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text.Text }, response.Metadata);
            yield return Event("text-end", id, new AITextEndEventData(), response.Metadata);
        }
        yield return Event("finish", null, new AIFinishEventData
        {
            FinishReason = response.Status == "failed" ? "error" : "stop", Model = response.Model,
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? "ainvented/workflow", DateTimeOffset.UtcNow,
                response.Usage, additionalProperties: new Dictionary<string, object?> { ["ainvented"] = response.Metadata ?? [] })
        }, response.Metadata);
    }
}
