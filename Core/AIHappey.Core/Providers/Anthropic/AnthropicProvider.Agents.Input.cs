using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    private sealed record ManagedAgentInputSubmission(List<object> Events, List<JsonElement> RecoveryEvents);

    private async Task<ManagedAgentInputSubmission> BuildManagedAgentInputSubmissionAsync(
        AIRequest request,
        AnthropicManagedAgentSessionResolution session,
        CancellationToken cancellationToken)
    {
        var items = request.Input?.Items ?? [];
        var clientParts = items.SelectMany(static item => item.Content ?? [])
            .OfType<AIToolCallContentPart>().Where(static part => part.IsClientToolCall).ToList();
        var history = !session.Created && clientParts.Count > 0
            ? await ListAllManagedAgentEventsAsync(session.Id, cancellationToken)
            : [];
        var calls = history.Where(static evt => TryGetString(evt, "type") == "agent.custom_tool_use")
            .ToDictionary(static evt => TryGetString(evt, "id")!, static evt => evt, StringComparer.Ordinal);
        var submittedIds = history.Where(static evt => TryGetString(evt, "type") == "user.custom_tool_result")
            .Select(static evt => TryGetString(evt, "custom_tool_use_id") ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var lastStatus = history.LastOrDefault(static evt => TryGetString(evt, "type") is
            "session.status_idle" or "session.status_running" or "session.status_terminated");
        var pendingIds = GetManagedAgentPendingEventIds(lastStatus);
        pendingIds.IntersectWith(calls.Keys);
        pendingIds.ExceptWith(submittedIds);

        var events = new List<object>();
        var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var boundary = -1;
        for (var index = 0; index < items.Count; index++)
        {
            foreach (var part in items[index].Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (!part.IsClientToolCall || !calls.ContainsKey(part.ToolCallId))
                    continue;
                boundary = Math.Max(boundary, index);
                if (!pendingIds.Contains(part.ToolCallId) || !HasManagedAgentClientResult(part))
                    continue;
                var result = BuildManagedAgentCustomToolResult(part);
                if (results.TryGetValue(part.ToolCallId, out var previous))
                {
                    if (!ManagedAgentJsonEquals(previous, result))
                        throw new InvalidOperationException($"Anthropic managed-agent custom tool '{part.ToolCallId}' has conflicting results in the request.");
                    continue;
                }
                results[part.ToolCallId] = result;
                events.Add(result);
            }
        }

        // A continuation is not a new user turn. The full browser transcript
        // includes the old prompt, which must not be submitted a second time.
        if (boundary < 0 && !session.Created)
            for (var index = 0; index < items.Count; index++)
                if (!string.Equals(items[index].Role, "user", StringComparison.OrdinalIgnoreCase))
                    boundary = index;
        var text = session.Created
            ? ExtractLatestManagedAgentUserText(request) ?? request.Input?.Text ?? request.Instructions
            : ExtractManagedAgentUserTextAfter(items, boundary)
                ?? (items.Count == 0 && results.Count == 0 ? request.Input?.Text ?? request.Instructions : null);
        if (!string.IsNullOrWhiteSpace(text))
            events.Add(new { type = "user.message", content = new[] { new { type = "text", text } } });

        if (events.Count == 0 && session.Created)
            throw new InvalidOperationException("Anthropic managed agents require a user message to start a session.");
        if (events.Count > 0)
            return new(events, []);

        // Retrying a transcript that already submitted a result must not invent
        // input or open a live stream that can wait forever on an idle session.
        // Reconcile the response from persisted history instead.
        if (history.Count > 0)
        {
            var marker = history.FindLastIndex(evt => TryGetString(evt, "type") == "user.custom_tool_result"
                && clientParts.Any(part => part.ToolCallId == TryGetString(evt, "custom_tool_use_id")));
            var recovery = marker >= 0 ? history.Skip(marker + 1).ToList() : new List<JsonElement>();
            foreach (var id in pendingIds)
                if (!recovery.Any(evt => TryGetString(evt, "id") == id))
                    recovery.Insert(0, calls[id]);
            if (lastStatus.ValueKind == JsonValueKind.Object
                && !recovery.Any(evt => TryGetString(evt, "id") == TryGetString(lastStatus, "id")))
                recovery.Add(lastStatus);
            return new([], recovery);
        }
        throw new InvalidOperationException("Anthropic managed-agent request contains no new user message or pending custom-tool result.");
    }

    private static string? ExtractManagedAgentUserTextAfter(List<AIInputItem> items, int boundary)
    {
        foreach (var item in items.Skip(boundary + 1).Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            var text = string.Join("\n", item.Content?.OfType<AITextContentPart>()
                .Select(static part => part.Text).Where(static text => !string.IsNullOrWhiteSpace(text)) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    private static HashSet<string> GetManagedAgentPendingEventIds(JsonElement evt)
    {
        if (TryGetString(evt, "type") != "session.status_idle"
            || !TryGetProperty(evt, "stop_reason", out var reason)
            || TryGetString(reason, "type") != "requires_action"
            || !TryGetProperty(reason, "event_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return new(StringComparer.Ordinal);
        return ids.EnumerateArray().Where(static id => id.ValueKind == JsonValueKind.String)
            .Select(static id => id.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static bool HasManagedAgentClientResult(AIToolCallContentPart part)
        => part.State is "output-available" or "output-error" or "output-denied"
            || part.Output is not null || part.Approval?.Approved == false;

    private static JsonElement BuildManagedAgentCustomToolResult(AIToolCallContentPart part)
    {
        var content = new List<object>();
        var isError = part.State is "output-error" or "output-denied" || part.Approval?.Approved == false;
        var output = JsonSerializer.SerializeToElement(part.Output, JsonSerializerOptions.Web);
        if (output.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            isError |= TryGetBool(output, "isError") == true || TryGetBool(output, "is_error") == true;
            if (TryGetProperty(output, "content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in blocks.EnumerateArray())
                    AddManagedAgentResultBlock(content, block);
                if (TryGetProperty(output, "structuredContent", out var structured)
                    && structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    AddManagedAgentResultText(content, structured.GetRawText());
            }
            else if (output.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in output.EnumerateArray())
                    AddManagedAgentResultBlock(content, block);
            }
            else
                AddManagedAgentResultText(content, output.ValueKind == JsonValueKind.String ? output.GetString() : output.GetRawText());
        }
        if (isError && content.Count == 0)
            AddManagedAgentResultText(content, part.Approval?.Reason ?? "Client tool execution failed or was denied.");
        return JsonSerializer.SerializeToElement(new
        {
            type = "user.custom_tool_result",
            custom_tool_use_id = part.ToolCallId,
            content,
            is_error = isError
        }, JsonSerializerOptions.Web);
    }

    private static void AddManagedAgentResultText(List<object> content, string? text)
    {
        if (!string.IsNullOrEmpty(text))
            content.Add(new { type = "text", text });
    }

    private static void AddManagedAgentResultBlock(List<object> content, JsonElement block)
    {
        var type = TryGetString(block, "type");
        if (type == "text")
        {
            AddManagedAgentResultText(content, TryGetString(block, "text"));
            return;
        }
        if (type is "image" or "document" && TryGetProperty(block, "source", out _)
            || type == "search_result")
        {
            content.Add(block.Clone());
            return;
        }
        if (type == "image" && TryGetString(block, "data") is { Length: > 0 } data
            && TryGetString(block, "mimeType") is { Length: > 0 } mediaType)
        {
            content.Add(new { type = "image", source = new { type = "base64", data, media_type = mediaType } });
            return;
        }
        if (type == "resource" && TryGetProperty(block, "resource", out var resource)
            && TryGetString(resource, "text") is { } resourceText)
        {
            if (!string.IsNullOrEmpty(resourceText))
                content.Add(new { type = "document", source = new { type = "text", data = resourceText, media_type = "text/plain" } });
            return;
        }
        // Unsupported MCP blocks still contribute their complete JSON rather
        // than being dropped or sent as an invalid Anthropic content type.
        AddManagedAgentResultText(content, block.ValueKind == JsonValueKind.String ? block.GetString() : block.GetRawText());
    }

    private async Task<string> SendManagedAgentInputEventsAsync(
        string sessionId,
        List<object> events,
        CancellationToken cancellationToken)
    {
        var response = await SendManagedAgentsJsonAsync(HttpMethod.Post,
            $"{ManagedAgentSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events", new { events },
            "Anthropic managed-agent send events", cancellationToken);
        if (!TryGetProperty(response, "data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Anthropic managed-agent send events response did not include sent events.");
        return data.EnumerateArray().Select(static evt => TryGetString(evt, "id"))
            .LastOrDefault(static id => !string.IsNullOrWhiteSpace(id))
            ?? throw new InvalidOperationException("Anthropic managed-agent send events response did not include a sent event id.");
    }
}
