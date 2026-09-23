using System.Diagnostics;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    private async Task<DevicAIAssistantExecution> RunAssistantAsync(
        AIRequest request,
        DevicAIRoute route,
        CancellationToken cancellationToken)
    {
        var hasExisting = TryFindChatUid(request, out var chatUid);
        JsonElement? baselineHistory = null;
        var baselineIds = new HashSet<string>(StringComparer.Ordinal);
        if (hasExisting)
        {
            baselineHistory = await GetChatHistoryAsync(route.Target, chatUid, cancellationToken);
            ValidateChatAssistant(baselineHistory.Value, route.Target);
            foreach (var message in GetArray(baselineHistory.Value, "chatContent"))
            {
                var uid = GetString(message, "uid");
                if (!string.IsNullOrWhiteSpace(uid)) baselineIds.Add(uid);
            }
        }

        var toolResponses = ExtractClientToolResponses(request);
        if (toolResponses.Count > 0)
        {
            if (!hasExisting)
                throw new InvalidOperationException("DevicAI client tool responses require a recovered chatUid.");
            var acknowledgement = await SendJsonAsync(
                HttpMethod.Post,
                $"assistants/{Uri.EscapeDataString(route.Target)}/chats/{Uri.EscapeDataString(chatUid)}/tool-response",
                new { responses = toolResponses },
                "submit tool responses",
                cancellationToken);
            var realtime = await PollAssistantUntilStableAsync(route.Target, chatUid, request, cancellationToken);
            var history = await GetChatHistoryAsync(route.Target, chatUid, cancellationToken);
            var toolContinuationMessages = FilterNewMessages(GetArray(history, "chatContent"), baselineIds);
            return new DevicAIAssistantExecution(
                route, chatUid, false, toolContinuationMessages, acknowledgement, history,
                GetString(realtime, "status"), []);
        }

        var attachments = await ResolveAssistantAttachmentsAsync(request, cancellationToken);
        var payload = BuildAssistantMessagePayload(request, hasExisting ? chatUid : null, attachments);
        var raw = await SendJsonAsync(
            HttpMethod.Post,
            $"assistants/{Uri.EscapeDataString(route.Target)}/messages",
            payload,
            "process assistant message",
            cancellationToken);
        if (raw.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("DevicAI synchronous assistant response was not a message array.");

        var messages = raw.EnumerateArray().Select(message => message.Clone()).ToList();
        chatUid = messages.Select(message => GetString(message, "chatUid"))
                      .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                  ?? (hasExisting ? chatUid : null)
                  ?? throw new InvalidOperationException("DevicAI assistant response did not include chatUid.");
        var historyAfter = await GetChatHistoryAsync(route.Target, chatUid, cancellationToken);
        ValidateChatAssistant(historyAfter, route.Target);
        return new DevicAIAssistantExecution(
            route, chatUid, !hasExisting, messages, raw.Clone(), historyAfter,
            "completed", attachments.Uploads);
    }

    private static Dictionary<string, object?> BuildAssistantMessagePayload(
        AIRequest request,
        string? chatUid,
        DevicAIAttachments attachments)
    {
        var payload = new Dictionary<string, object?>
        {
            ["message"] = ExtractLatestUserText(request)
        };
        if (!string.IsNullOrWhiteSpace(chatUid))
            payload["chatUid"] = chatUid;
        if (attachments.Files.Count > 0)
            payload["files"] = attachments.Files;
        if (attachments.Images.Count > 0)
            payload["images"] = attachments.Images;

        var options = GetDevicAIOptions(request);
        CopyOption(options, payload, "userName", "user_name");
        CopyOption(options, payload, "tags");
        CopyOption(options, payload, "metadata");
        CopyOption(options, payload, "tenantId", "tenant_id");
        CopyOption(options, payload, "subtenantId", "subtenant_id");
        CopyOption(options, payload, "enabledTools", "enabled_tools");
        CopyOption(options, payload, "disabledIntegrations", "disabled_integrations");
        CopyOption(options, payload, "provider");
        CopyOption(options, payload, "model");
        CopyOption(options, payload, "transcriptId", "transcript_id");

        if (string.IsNullOrWhiteSpace(chatUid))
        {
            var previous = BuildPreviousConversation(request);
            if (previous.Count > 0)
                payload["previousConversation"] = previous;
        }

        if (request.Tools is { Count: > 0 })
            payload["tools"] = request.Tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description ?? tool.Title ?? tool.Name,
                    parameters = tool.InputSchema ?? new { type = "object", properties = new { } }
                }
            }).ToList();
        return payload;
    }

    private static List<object> BuildPreviousConversation(AIRequest request)
    {
        var candidates = request.Input?.Items ?? [];
        var latestUserIndex = candidates.FindLastIndex(item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var previous = new List<object>();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (index == latestUserIndex)
                continue;
            var role = candidates[index].Role?.ToLowerInvariant();
            if (role is not ("user" or "assistant"))
                continue;
            var text = JoinText(candidates[index].Content);
            if (!string.IsNullOrWhiteSpace(text))
                previous.Add(new { role, message = text });
        }
        return previous;
    }

    private static List<object> ExtractClientToolResponses(AIRequest request)
    {
        var responses = new List<object>();
        foreach (var tool in (request.Input?.Items ?? [])
                     .SelectMany(item => item.Content?.OfType<AIToolCallContentPart>() ?? []))
        {
            if (tool.ProviderExecuted == true || tool.Output is null)
                continue;
            responses.Add(new
            {
                tool_call_id = tool.ToolCallId,
                content = ExtractToolResultContent(tool.Output),
                role = "tool"
            });
        }
        return responses;
    }

    private static object ExtractToolResultContent(object output)
    {
        var json = output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, DevicAIJson);
        if (json.ValueKind == JsonValueKind.Object
            && TryGetProperty(json, "structuredContent", out var structured)
            && structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return structured.Clone();
        return json.Clone();
    }

    private async Task<JsonElement> GetChatHistoryAsync(
        string identifier,
        string chatUid,
        CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Get,
            $"assistants/{Uri.EscapeDataString(identifier)}/chats/{Uri.EscapeDataString(chatUid)}",
            null,
            "get chat history",
            cancellationToken);

    private async Task<JsonElement> GetRealtimeChatAsync(
        string identifier,
        string chatUid,
        CancellationToken cancellationToken)
        => await SendJsonAsync(
            HttpMethod.Get,
            $"assistants/{Uri.EscapeDataString(identifier)}/chats/{Uri.EscapeDataString(chatUid)}/realtime",
            null,
            "get realtime chat",
            cancellationToken);

    private async Task<JsonElement> PollAssistantUntilStableAsync(
        string identifier,
        string chatUid,
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeout = ResolvePollTimeout(request);
        while (true)
        {
            var realtime = await GetRealtimeChatAsync(identifier, chatUid, cancellationToken);
            var status = GetString(realtime, "status");
            if (status is not null
                && (TerminalChatStatuses.Contains(status)
                    || string.Equals(status, "waiting_for_tool_response", StringComparison.OrdinalIgnoreCase)))
                return realtime;
            if (stopwatch.Elapsed >= timeout)
                throw new TimeoutException($"DevicAI chat '{chatUid}' did not reach a stable status within {timeout}.");
            await Task.Delay(ResolvePollInterval(request), cancellationToken);
        }
    }

    private static IReadOnlyList<JsonElement> FilterNewMessages(
        IReadOnlyList<JsonElement> messages,
        HashSet<string> baselineIds)
    {
        if (baselineIds.Count == 0)
            return messages;
        return messages.Where(message =>
        {
            var uid = GetString(message, "uid");
            return string.IsNullOrWhiteSpace(uid) || !baselineIds.Contains(uid);
        }).ToList();
    }

    private static void ValidateChatAssistant(JsonElement history, string expectedIdentifier)
    {
        var identifier = GetString(history, "assistantSpecializationIdentifier");
        if (!string.IsNullOrWhiteSpace(identifier)
            && !string.Equals(identifier, expectedIdentifier, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"DevicAI chat belongs to assistant '{identifier}', not requested assistant '{expectedIdentifier}'.");
    }
}
