using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    private sealed record ContentSlot(string Kind, string Id, System.Text.StringBuilder? Text = null);

    private static JsonObject? FindIdentity(AIRequest request, Route route)
    {
        var candidates = new List<JsonObject>();
        void Add(object? value)
        {
            var node = Node(value);
            if (node is JsonObject outer && outer["dust"] is JsonObject nested)
                node = Str(nested, "conversationId") is null && nested["raw"] is JsonObject raw ? raw : nested;
            if (node is JsonObject obj && OptionString(obj, "conversationId", "conversation_id") is { } conversation)
            {
                var identity = (JsonObject)obj.DeepClone();
                identity["conversationId"] = conversation;
                if (OptionString(obj, "workspaceId", "workspace_id") is { } workspace)
                    identity["workspaceId"] = workspace;
                if (OptionString(obj, "agentId", "agent_id") is { } agent)
                    identity["agentId"] = agent;
                candidates.Add(identity);
            }
        }
        Add(request.Input?.Metadata);
        foreach (var item in request.Input?.Items ?? [])
        {
            Add(item.Metadata);
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true) continue;
                Add(tool.Metadata);
                if (tool.ToolName != IdentityTool) continue;
                if (tool.Output is CallToolResult result)
                    Add(result.StructuredContent);
                else
                {
                    var output = Node(tool.Output);
                    Add(output is JsonObject wrapped && wrapped["structuredContent"] is { } structured ? structured : output);
                }
            }
        }
        Add(Options(request));
        foreach (var candidate in candidates)
        {
            if (Str(candidate, "workspaceId") is { } workspace && workspace != route.WorkspaceId
                || Str(candidate, "agentId") is { } agent && agent != route.AgentId)
                throw new ArgumentException("Dust conversation identity belongs to a different workspace or agent.");
            Segment(Str(candidate, "conversationId")!);
        }
        if (candidates.Select(c => Str(c, "conversationId")).Distinct().Count() > 1)
            throw new ArgumentException("Conflicting Dust conversation identities.");
        if (candidates.Count == 0) return null;
        var combined = new JsonObject();
        foreach (var candidate in candidates)
            foreach (var field in candidate)
                combined[field.Key] = field.Value?.DeepClone();
        return combined;
    }

    private static JsonObject Identity(Route route, string conversationId)
        => new()
        {
            ["workspaceId"] = route.WorkspaceId,
            ["agentId"] = route.AgentId,
            ["conversationId"] = conversationId
        };

    private IEnumerable<AIStreamEvent> IdentityEvents(Route route, string conversationId)
    {
        var identity = Identity(route, conversationId);
        var callId = $"dust-conversation-{conversationId}";
        yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
        {
            ToolName = IdentityTool,
            Input = new { workspaceId = route.WorkspaceId, agentId = route.AgentId },
            ProviderExecuted = true,
            ProviderMetadata = ScopedMetadata(identity)
        }, identity);
        yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
        {
            ToolName = IdentityTool,
            Output = new CallToolResult { StructuredContent = System.Text.Json.JsonSerializer.SerializeToElement(identity, Json) },
            ProviderExecuted = true,
            ProviderMetadata = ScopedMetadata(identity)
        }, identity);
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var content = new List<AIContentPart>();
        var tools = new Dictionary<string, AIToolCallContentPart>();
        var order = new List<ContentSlot>();
        void Append(string kind, string id, string delta)
        {
            var slot = order.LastOrDefault();
            if (slot?.Kind != kind || slot.Id != id)
            {
                slot = new ContentSlot(kind, id, new System.Text.StringBuilder());
                order.Add(slot);
            }
            slot.Text!.Append(delta);
        }
        void Reconcile(string kind, string value)
        {
            var slots = order.Where(slot => slot.Kind == kind).ToList();
            if (string.Concat(slots.Select(slot => slot.Text!.ToString())) == value) return;
            if (slots.Count == 0)
            {
                order.Add(new ContentSlot(kind, "dust-final-" + kind, new System.Text.StringBuilder(value)));
                return;
            }
            slots[0].Text!.Clear().Append(value);
            foreach (var slot in slots.Skip(1)) order.Remove(slot);
        }
        Dictionary<string, object?>? metadata = null;
        var status = "in_progress";
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
        {
            metadata = part.Metadata;
            var id = part.Event.Id ?? "";
            switch (part.Event.Data)
            {
                case AITextDeltaEventData delta:
                    Append("text", id, delta.Delta);
                    break;
                case AIReasoningDeltaEventData delta:
                    Append("reasoning", id, delta.Delta);
                    break;
                case AIToolInputAvailableEventData input:
                    if (!tools.ContainsKey(id)) order.Add(new ContentSlot("tool", id));
                    tools[id] = new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = id, ToolName = input.ToolName, Input = input.Input,
                        ProviderExecuted = true, State = "input-available", Metadata = metadata
                    };
                    break;
                case AIToolOutputAvailableEventData output when tools.TryGetValue(id, out var tool):
                    tools[id] = new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = id, ToolName = tool.ToolName, Input = tool.Input,
                        Output = output.Output, ProviderExecuted = true, State = "output-available", Metadata = metadata
                    };
                    break;
                case AIToolOutputErrorEventData error when tools.TryGetValue(id, out var failed):
                    tools[id] = new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = id, ToolName = failed.ToolName, Input = failed.Input,
                        Output = new { error = error.ErrorText }, ProviderExecuted = true, State = "output-error", Metadata = metadata
                    };
                    break;
                case AIToolApprovalRequestEventData approval when tools.TryGetValue(approval.ToolCallId, out var pending):
                    tools[approval.ToolCallId] = new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = pending.ToolCallId, ToolName = pending.ToolName, Input = pending.Input,
                        ProviderExecuted = true, State = "approval-requested", Metadata = metadata,
                        Approval = new AIToolCallApproval { Id = approval.ApprovalId }
                    };
                    break;
                case AIFinishEventData finish:
                    status = finish.FinishReason == "tool-calls" ? "requires_action" : "completed";
                    if (Node(metadata) is JsonObject finalMetadata
                        && Str(finalMetadata["dust"], "outputText") is { } finalText)
                    {
                        Reconcile("text", finalText);
                    }
                    if (Node(metadata) is JsonObject reasoningMetadata
                        && Str(reasoningMetadata["dust"], "outputReasoning") is { } finalReasoning)
                    {
                        Reconcile("reasoning", finalReasoning);
                    }
                    break;
                case AIErrorEventData error:
                    throw new InvalidOperationException(error.ErrorText);
            }
        }
        foreach (var slot in order)
        {
            if (slot.Kind == "tool") content.Add(tools[slot.Id]);
            else if (slot.Kind == "text")
                content.Add(new AITextContentPart { Type = "text", Text = slot.Text!.ToString() });
            else if (slot.Kind == "reasoning")
                content.Add(new AIReasoningContentPart { Type = "reasoning", Text = slot.Text!.ToString() });
        }
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = status, Metadata = metadata,
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }],
                Metadata = metadata
            }
        };
    }
}
