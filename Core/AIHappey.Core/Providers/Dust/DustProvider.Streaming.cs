using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    private async IAsyncEnumerable<AIStreamEvent> StreamMessageAsync(
        Route route, string conversationId, string messageId, string? cursor, List<Approval> submitted,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var metadata = Identity(route, conversationId);
        metadata["messageId"] = messageId;
        var textId = $"dust-text-{messageId}";
        var text = new StringBuilder();
        var started = false;
        var knownTools = new HashSet<string>();
        var pendingApprovals = new HashSet<string>();
        var skipping = cursor is null
            ? submitted.Where(approval => approval.MessageId == messageId).Select(approval => approval.ActionId).ToHashSet()
            : [];
        while (true)
        {
            var path = $"{route.Path}/assistant/conversations/{Segment(conversationId)}/messages/{Segment(messageId)}/events";
            if (cursor is not null) path += "?lastEventId=" + Uri.EscapeDataString(cursor);
            using var request = CreateRequest(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var reconnect = false;
            await foreach (var frame in ReadEventsAsync(response, ct))
            {
                if (Str(frame, "type") == "dust_connection_done")
                {
                    reconnect = true;
                    break;
                }
                cursor = Str(frame, "eventId") ?? cursor;
                var data = frame["data"] as JsonObject ?? frame as JsonObject
                    ?? throw new InvalidOperationException("Invalid Dust SSE payload.");
                var type = Str(data, "type");
                if (Str(data, "configurationId") is { } configuration && configuration != route.AgentId
                    || Str(data, "messageId") is { } nativeMessage && nativeMessage != messageId
                    || Str(data, "conversationId") is { } nativeConversation && nativeConversation != conversationId)
                    throw new InvalidOperationException("Dust SSE event identity does not match the requested conversation, message, or agent.");
                metadata["lastEventId"] = cursor;
                metadata["raw"] = data.DeepClone();
                if (skipping.Count > 0)
                {
                    if (type == "tool_approve_execution" && Str(data, "actionId") is { } oldAction)
                        skipping.Remove(oldAction);
                    continue;
                }
                if (type is "agent_error" or "user_message_error")
                    throw new InvalidOperationException(Str(data["error"], "message") ?? "Dust agent execution failed.");
                if (type == "agent_generation_cancelled")
                    throw new InvalidOperationException("Dust agent execution was cancelled.");

                if (type == "generation_tokens" && Str(data, "classification") == "tokens")
                {
                    if (!started)
                    {
                        yield return Event("text-start", textId, new AITextStartEventData(), metadata);
                        started = true;
                    }
                    var delta = Str(data, "text") ?? "";
                    text.Append(delta);
                    yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = delta }, metadata);
                }
                else if (type is "tool_params" or "agent_action_success")
                {
                    var action = data["action"] as JsonObject
                        ?? throw new InvalidOperationException("Dust tool event did not include an action.");
                    var actionId = Str(action, "sId")
                        ?? throw new InvalidOperationException("Dust tool action did not include sId.");
                    Segment(actionId);
                    var name = Str(action, "toolName") ?? Str(action, "functionCallName") ?? "dust_tool";
                    metadata["actionId"] = actionId;
                    if (knownTools.Add(actionId))
                        yield return Event("tool-input-available", actionId, new AIToolInputAvailableEventData
                        {
                            ToolName = name, Input = action["params"]?.DeepClone() ?? new JsonObject(),
                            ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                        }, metadata);
                    if (type == "agent_action_success")
                    {
                        pendingApprovals.Remove(actionId);
                        yield return Event("tool-output-available", actionId, new AIToolOutputAvailableEventData
                        {
                            ToolName = name, Output = action["output"]?.DeepClone() ?? action.DeepClone(),
                            ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                        }, metadata);
                    }
                }
                else if (type == "tool_approve_execution")
                {
                    var actionId = Str(data, "actionId")
                        ?? throw new InvalidOperationException("Dust approval event did not include actionId.");
                    Segment(actionId);
                    if (submitted.Any(approval => approval.MessageId == messageId && approval.ActionId == actionId))
                        continue;
                    metadata["actionId"] = actionId;
                    metadata["approvalId"] = $"{messageId}:{actionId}";
                    var name = Str(data["metadata"], "toolName") ?? "dust_tool";
                    knownTools.Add(actionId);
                    pendingApprovals.Add(actionId);
                    yield return Event("tool-input-available", actionId, new AIToolInputAvailableEventData
                    {
                        ToolName = name, Input = data["inputs"]?.DeepClone() ?? new JsonObject(),
                        ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                    }, metadata);
                    yield return Event("tool-approval-request", actionId, new AIToolApprovalRequestEventData
                    {
                        ApprovalId = $"{messageId}:{actionId}", ToolCallId = actionId
                    }, metadata);
                    if (data["isLastBlockingEventForStep"]?.GetValue<bool>() != false)
                    {
                        if (started)
                            yield return Event("text-end", textId, new AITextEndEventData(), metadata);
                        yield return Event("finish", messageId, new AIFinishEventData
                        {
                            FinishReason = "tool-calls", Model = route.Model,
                            MessageMetadata = AIFinishMessageMetadata.Create(route.Model, DateTimeOffset.UtcNow)
                        }, metadata);
                        yield break;
                    }
                }
                else if (type is "tool_personal_auth_required" or "tool_file_auth_required" or "tool_ask_user_question")
                    throw new NotSupportedException($"Dust requires a native interaction not supported by this gateway: {type}.");
                else if (type == "tool_error")
                {
                    var id = Str(data, "actionId") ?? knownTools.LastOrDefault() ?? $"dust-tool-error-{messageId}";
                    yield return Event("tool-output-error", id, new AIToolOutputErrorEventData
                    {
                        ToolCallId = id, ErrorText = Str(data["error"], "message") ?? "Dust tool failed.",
                        ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                    }, metadata);
                }
                else if (type is "agent_message_success" or "agent_message_gracefully_stopped" or "agent_message_done")
                {
                    if (pendingApprovals.Count > 0)
                        throw new InvalidOperationException("Dust ended a message while tool approvals were unresolved.");
                    var final = Str(data["message"], "content");
                    if (final is not null && final.StartsWith(text.ToString(), StringComparison.Ordinal))
                    {
                        var delta = final[text.Length..];
                        if (delta.Length > 0)
                        {
                            if (!started)
                                yield return Event("text-start", textId, new AITextStartEventData(), metadata);
                            started = true;
                            yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = delta }, metadata);
                        }
                    }
                    if (final is not null) metadata["outputText"] = final;
                    if (started) yield return Event("text-end", textId, new AITextEndEventData(), metadata);
                    yield return Event("finish", messageId, new AIFinishEventData
                    {
                        FinishReason = "stop", Model = route.Model,
                        MessageMetadata = AIFinishMessageMetadata.Create(route.Model, DateTimeOffset.UtcNow)
                    }, metadata);
                    yield break;
                }
            }
            if (!reconnect)
                throw new IOException("Dust SSE stream ended before a terminal agent event.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
