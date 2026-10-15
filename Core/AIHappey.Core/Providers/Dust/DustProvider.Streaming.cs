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
        metadata["streamMessageId"] = messageId;
        var textId = $"dust-text-{messageId}";
        var text = new StringBuilder();
        var started = false;
        var reasoning = new StringBuilder();
        var reasoningStarted = false;
        var reasoningId = $"dust-reasoning-{messageId}";
        var knownTools = new HashSet<string>();
        var completedTools = new HashSet<string>();
        var actionCalls = new Dictionary<string, string>();
        var pendingApprovals = new HashSet<string>();
        var skipping = cursor is null
            ? submitted.Where(approval => approval.StreamMessageId == messageId || approval.MessageId == messageId)
                .Select(approval => approval.ActionId).ToHashSet()
            : [];
        while (true)
        {
            var path = $"{route.Path}/assistant/conversations/{Segment(conversationId)}/messages/{Segment(messageId)}/events";
            if (cursor is not null) path += "?lastEventId=" + Uri.EscapeDataString(cursor);
            using var response = await SendEventsAsync(path, ct);
            response.EnsureSuccessStatusCode();
            var reconnect = false;
            await foreach (var frame in ReadEventsAsync(response, ct))
            {
                if (Str(frame, "type") == "dust_connection_done")
                {
                    if (pendingApprovals.Count > 0)
                    {
                        if (started)
                            yield return Event("text-end", textId, new AITextEndEventData(), metadata);
                        if (reasoningStarted)
                            yield return Event("reasoning-end", reasoningId, new AIReasoningEndEventData(), metadata);
                        yield return Event("finish", messageId, new AIFinishEventData
                        {
                            FinishReason = "tool-calls", Model = route.Model,
                            MessageMetadata = AIFinishMessageMetadata.Create(route.Model, DateTimeOffset.UtcNow,
                                additionalProperties: Metadata(metadata))
                        }, metadata);
                        yield break;
                    }
                    reconnect = true;
                    break;
                }
                cursor = Str(frame, "eventId") ?? cursor;
                var data = frame["data"] as JsonObject ?? frame as JsonObject
                    ?? throw new InvalidOperationException("Invalid Dust SSE payload.");
                var type = Str(data, "type");
                if (type != "tool_approve_execution" && (Str(data, "configurationId") is { } configuration && configuration != route.AgentId
                    || Str(data, "messageId") is { } nativeMessage && nativeMessage != messageId
                    || Str(data, "conversationId") is { } nativeConversation && nativeConversation != conversationId))
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

                if (type == "generation_tokens" && Str(data, "classification") == "chain_of_thought")
                {
                    if (!reasoningStarted)
                    {
                        yield return Event("reasoning-start", reasoningId, new AIReasoningStartEventData(), metadata);
                        reasoningStarted = true;
                    }
                    var delta = Str(data, "text") ?? "";
                    reasoning.Append(delta);
                    yield return Event("reasoning-delta", reasoningId, new AIReasoningDeltaEventData { Delta = delta }, metadata);
                }
                else if (type == "generation_tokens" && Str(data, "classification") == "tokens")
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
                else if (type == "tool_call_started")
                {
                    var callId = Str(data, "toolCallId");
                    if (callId is not null)
                        yield return Event("tool-input-start", callId, new AIToolInputStartEventData
                        {
                            ToolName = Str(data, "toolName") ?? "dust_tool",
                            ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                        }, metadata);
                }
                else if (type is "tool_params" or "agent_action_success")
                {
                    var action = data["action"] as JsonObject
                        ?? throw new InvalidOperationException("Dust tool event did not include an action.");
                    var actionId = Str(action, "sId")
                        ?? throw new InvalidOperationException("Dust tool action did not include sId.");
                    Segment(actionId);
                    var callId = Str(action, "functionCallId") ?? actionId;
                    actionCalls[actionId] = callId;
                    var name = Str(action, "toolName") ?? Str(action, "functionCallName") ?? "dust_tool";
                    metadata["actionId"] = actionId;
                    if (knownTools.Add(callId))
                        yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
                        {
                            ToolName = name, Input = action["params"]?.DeepClone() ?? new JsonObject(),
                            ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                        }, metadata);
                    if (type == "agent_action_success")
                    {
                        completedTools.Add(actionId);
                        pendingApprovals.Remove(actionId);
                        yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
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
                    var approvalConversation = Str(data, "conversationId") ?? conversationId;
                    var approvalMessage = Str(data, "messageId") ?? messageId;
                    Segment(approvalConversation);
                    Segment(approvalMessage);
                    if (submitted.Any(approval => approval.MessageId == approvalMessage && approval.ActionId == actionId))
                        continue;
                    metadata["actionId"] = actionId;
                    metadata["approvalConversationId"] = approvalConversation;
                    metadata["approvalMessageId"] = approvalMessage;
                    metadata["approvalId"] = $"{approvalConversation}:{approvalMessage}:{actionId}";
                    var name = Str(data["metadata"], "toolName") ?? "dust_tool";
                    var callId = actionCalls.GetValueOrDefault(actionId) ?? actionId;
                    knownTools.Add(callId);
                    pendingApprovals.Add(actionId);
                    yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
                    {
                        ToolName = name, Input = data["inputs"]?.DeepClone() ?? new JsonObject(),
                        ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                    }, metadata);
                    yield return Event("tool-approval-request", callId, new AIToolApprovalRequestEventData
                    {
                        ApprovalId = $"{approvalConversation}:{approvalMessage}:{actionId}", ToolCallId = callId
                    }, metadata);
                    if (data["isLastBlockingEventForStep"]?.GetValue<bool>() == true)
                    {
                        if (started)
                            yield return Event("text-end", textId, new AITextEndEventData(), metadata);
                        if (reasoningStarted)
                            yield return Event("reasoning-end", reasoningId, new AIReasoningEndEventData(), metadata);
                        yield return Event("finish", messageId, new AIFinishEventData
                        {
                            FinishReason = "tool-calls", Model = route.Model,
                            MessageMetadata = AIFinishMessageMetadata.Create(route.Model, DateTimeOffset.UtcNow,
                                additionalProperties: Metadata(metadata))
                        }, metadata);
                        yield break;
                    }
                }
                else if (type is "tool_personal_auth_required" or "tool_file_auth_required" or "tool_ask_user_question")
                    throw new NotSupportedException($"Dust requires a native interaction not supported by this gateway: {type}.");
                else if (type == "tool_error")
                {
                    var actionId = Str(data, "actionId") ?? Str(data["error"]?["metadata"], "actionId");
                    var id = actionId is not null ? actionCalls.GetValueOrDefault(actionId) ?? actionId
                        : knownTools.Count == 1 ? knownTools.Single() : $"dust-tool-error-{messageId}";
                    if (knownTools.Add(id))
                        yield return Event("tool-input-available", id, new AIToolInputAvailableEventData
                        {
                            ToolName = "dust_tool", Input = new JsonObject(),
                            ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                        }, metadata);
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
                    var finalMessage = data["message"] as JsonObject;
                    if (type == "agent_message_done")
                    {
                        var snapshot = await SendJsonAsync(HttpMethod.Get,
                            $"{route.Path}/assistant/conversations/{Segment(conversationId)}", null, ct);
                        finalMessage = (snapshot["conversation"]?["content"] as JsonArray)?
                            .OfType<JsonArray>().SelectMany(row => row.OfType<JsonObject>())
                            .LastOrDefault(message => Str(message, "sId") == messageId);
                        if (Str(finalMessage, "status") != "succeeded")
                            throw new InvalidOperationException("Dust ended the message without a successful final state.");
                    }
                    if (finalMessage?["actions"] is JsonArray finalActions)
                    {
                        foreach (var action in finalActions.OfType<JsonObject>())
                        {
                            var actionId = Str(action, "sId");
                            if (actionId is null || completedTools.Contains(actionId)) continue;
                            Segment(actionId);
                            var callId = Str(action, "functionCallId") ?? actionId;
                            metadata["actionId"] = actionId;
                            var name = Str(action, "toolName") ?? "dust_tool";
                            if (knownTools.Add(callId))
                                yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
                                {
                                    ToolName = name, Input = action["params"]?.DeepClone() ?? new JsonObject(),
                                    ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                                }, metadata);
                            if (Str(action, "status") == "failed")
                                yield return Event("tool-output-error", callId, new AIToolOutputErrorEventData
                                {
                                    ToolCallId = callId, ErrorText = "Dust tool execution failed.",
                                    ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                                }, metadata);
                            else if (action["output"] is not null)
                                yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
                                {
                                    ToolName = name, Output = action["output"]!.DeepClone(),
                                    ProviderExecuted = true, ProviderMetadata = ScopedMetadata(metadata)
                                }, metadata);
                        }
                    }
                    var final = Str(finalMessage, "content");
                    var finalReasoning = Str(finalMessage, "chainOfThought");
                    if (finalReasoning is not null && finalReasoning.StartsWith(reasoning.ToString(), StringComparison.Ordinal))
                    {
                        var delta = finalReasoning[reasoning.Length..];
                        if (delta.Length > 0)
                        {
                            if (!reasoningStarted)
                                yield return Event("reasoning-start", reasoningId, new AIReasoningStartEventData(), metadata);
                            reasoningStarted = true;
                            yield return Event("reasoning-delta", reasoningId, new AIReasoningDeltaEventData { Delta = delta }, metadata);
                        }
                    }
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
                    if (finalReasoning is not null) metadata["outputReasoning"] = finalReasoning;
                    if (reasoningStarted)
                        yield return Event("reasoning-end", reasoningId, new AIReasoningEndEventData(), metadata);
                    if (started) yield return Event("text-end", textId, new AITextEndEventData(), metadata);
                    yield return Event("finish", messageId, new AIFinishEventData
                    {
                        FinishReason = "stop", Model = route.Model,
                        MessageMetadata = AIFinishMessageMetadata.Create(route.Model, DateTimeOffset.UtcNow,
                            additionalProperties: Metadata(metadata))
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
