using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    private static JsonObject? FindIdentity(AIRequest request, Route route)
    {
        var candidates = new List<JsonObject>();
        void Add(object? value)
        {
            var node = Node(value);
            if (node is JsonObject outer && outer["dust"] is JsonObject nested)
                node = nested["raw"] is JsonObject raw ? raw : nested;
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
        return candidates.LastOrDefault();
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
        var texts = new Dictionary<string, System.Text.StringBuilder>();
        Dictionary<string, object?>? metadata = null;
        var status = "in_progress";
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
        {
            metadata = part.Metadata;
            var id = part.Event.Id ?? "";
            switch (part.Event.Data)
            {
                case AITextDeltaEventData delta:
                    if (!texts.TryGetValue(id, out var text)) texts[id] = text = new();
                    text.Append(delta.Delta);
                    break;
                case AIToolInputAvailableEventData input:
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
                        texts.Clear();
                        texts[id] = new System.Text.StringBuilder(finalText);
                    }
                    break;
            }
        }
        content.AddRange(tools.Values);
        content.AddRange(texts.Values.Select(text => new AITextContentPart { Type = "text", Text = text.ToString() }));
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
