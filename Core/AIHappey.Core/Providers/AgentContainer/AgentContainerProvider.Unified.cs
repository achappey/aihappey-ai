using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AgentContainer;

public partial class AgentContainerProvider
{
    private const string ConversationIdentityTool = "create_agentcontainer_conversation";
    private const string TaskIdentityTool = "dispatch_agentcontainer_task";

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        var execution = await RunAgentContainerAsync(request, cancellationToken);
        return CreateAgentContainerResponse(execution);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var execution = await RunAgentContainerAsync(request, cancellationToken);
        var timestamp = DateTimeOffset.UtcNow;

        if (execution.Created)
        {
            foreach (var evt in CreateAgentContainerIdentityEvents(execution, timestamp))
                yield return evt;
        }

        foreach (var activity in execution.Activities)
        {
            var callId = GetAgentContainerString(activity, "callId")
                         ?? GetAgentContainerString(activity, "id")
                         ?? $"agentcontainer-activity-{Guid.NewGuid():N}";
            var toolKey = GetAgentContainerString(activity, "toolKey") ?? "agentcontainer_tool";
            var activityStatus = GetAgentContainerString(activity, "status");
            var scoped = CreateAgentContainerScopedMetadata(activity);
            yield return CreateAgentContainerStreamEvent(
                "tool-input-available",
                callId,
                new AIToolInputAvailableEventData
                {
                    ToolName = NormalizeAgentContainerToolName(toolKey),
                    Title = toolKey,
                    Input = TryGetAgentContainerProperty(activity, "input", out var input) ? input.Clone() : new { },
                    ProviderExecuted = true,
                    ProviderMetadata = scoped
                },
                timestamp,
                CreateAgentContainerMetadata(execution));

            if (string.Equals(activityStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                yield return CreateAgentContainerStreamEvent(
                    "tool-output-error",
                    callId,
                    new AIToolOutputErrorEventData
                    {
                        ToolCallId = callId,
                        ErrorText = GetAgentContainerString(activity, "errorCode") ?? "AgentContainer tool failed.",
                        ProviderExecuted = true,
                        Dynamic = true,
                        ProviderMetadata = scoped
                    },
                    timestamp,
                    CreateAgentContainerMetadata(execution));
            }
            else
            {
                yield return CreateAgentContainerStreamEvent(
                    "tool-output-available",
                    callId,
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = NormalizeAgentContainerToolName(toolKey),
                        Output = new CallToolResult { StructuredContent = activity.Clone() },
                        ProviderExecuted = true,
                        Dynamic = true,
                        ProviderMetadata = scoped
                    },
                    timestamp,
                    CreateAgentContainerMetadata(execution));
            }
        }

        foreach (var message in execution.Messages.Where(message =>
                     string.Equals(GetAgentContainerString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase)))
        {
            var text = GetAgentContainerString(message, "content");
            if (string.IsNullOrEmpty(text))
                continue;
            var id = GetAgentContainerString(message, "id") ?? $"agentcontainer-text-{Guid.NewGuid():N}";
            var loose = CreateAgentContainerLooseMetadata(message);
            yield return CreateAgentContainerStreamEvent("text-start", id,
                new AITextStartEventData { ProviderMetadata = loose }, timestamp, CreateAgentContainerMetadata(execution));
            yield return CreateAgentContainerStreamEvent("text-delta", id,
                new AITextDeltaEventData { Delta = text, ProviderMetadata = loose }, timestamp, CreateAgentContainerMetadata(execution));
            yield return CreateAgentContainerStreamEvent("text-end", id,
                new AITextEndEventData { ProviderMetadata = loose }, timestamp, CreateAgentContainerMetadata(execution));
        }

        if (!execution.Route.IsConversation)
        {
            var result = GetAgentContainerString(execution.Task, "result");
            if (!string.IsNullOrEmpty(result))
            {
                var id = $"agentcontainer-result-{GetAgentContainerString(execution.Task, "id")}";
                var loose = CreateAgentContainerLooseMetadata(execution.Task);
                yield return CreateAgentContainerStreamEvent("text-start", id,
                    new AITextStartEventData { ProviderMetadata = loose }, timestamp, CreateAgentContainerMetadata(execution));
                yield return CreateAgentContainerStreamEvent("text-delta", id,
                    new AITextDeltaEventData { Delta = result, ProviderMetadata = loose }, timestamp, CreateAgentContainerMetadata(execution));
                yield return CreateAgentContainerStreamEvent("text-end", id,
                    new AITextEndEventData { ProviderMetadata = loose }, timestamp, CreateAgentContainerMetadata(execution));
            }
        }

        foreach (var file in execution.Files)
        {
            yield return CreateAgentContainerStreamEvent(
                "file",
                file.Id,
                new AIFileEventData
                {
                    MediaType = file.MediaType,
                    Filename = file.Filename,
                    Url = $"data:{file.MediaType};base64,{Convert.ToBase64String(file.Bytes)}",
                    ProviderMetadata = CreateAgentContainerScopedMetadata(file.Raw)
                },
                timestamp,
                CreateAgentContainerMetadata(execution));
        }

        var status = GetAgentContainerString(execution.Task, "status");
        var error = GetAgentContainerString(execution.Task, "error");
        if (status is "failed" or "cancelled" or "expired")
        {
            yield return CreateAgentContainerStreamEvent("error", GetAgentContainerString(execution.Task, "id"),
                new AIErrorEventData { ErrorText = error ?? $"AgentContainer task ended with status {status}." },
                timestamp, CreateAgentContainerMetadata(execution));
            yield break;
        }

        yield return CreateAgentContainerStreamEvent(
            "finish",
            GetAgentContainerString(execution.Task, "id"),
            CreateAgentContainerFinishData(execution, timestamp),
            timestamp,
            CreateAgentContainerMetadata(execution));
    }

    private AIResponse CreateAgentContainerResponse(AgentContainerExecution execution)
    {
        var content = new List<AIContentPart>();
        if (execution.Created)
            content.Add(CreateAgentContainerIdentityToolPart(execution));
        content.AddRange(execution.Activities.Select(CreateAgentContainerActivityPart));

        foreach (var message in execution.Messages.Where(message =>
                     string.Equals(GetAgentContainerString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase)))
        {
            var text = GetAgentContainerString(message, "content");
            if (!string.IsNullOrEmpty(text))
                content.Add(new AITextContentPart
                {
                    Type = "text",
                    Text = text,
                    Metadata = new Dictionary<string, object?> { ["agentcontainer.raw"] = message.Clone() }
                });
        }

        if (!execution.Route.IsConversation)
        {
            var result = GetAgentContainerString(execution.Task, "result");
            if (!string.IsNullOrEmpty(result))
                content.Add(new AITextContentPart
                {
                    Type = "text",
                    Text = result,
                    Metadata = new Dictionary<string, object?> { ["agentcontainer.raw"] = execution.Task.Clone() }
                });
        }

        foreach (var file in execution.Files)
            content.Add(new AIFileContentPart
            {
                Type = "file",
                Filename = file.Filename,
                MediaType = file.MediaType,
                Data = $"data:{file.MediaType};base64,{Convert.ToBase64String(file.Bytes)}",
                Metadata = new Dictionary<string, object?> { ["agentcontainer.raw"] = file.Raw.Clone() }
            });

        var metadata = CreateAgentContainerMetadata(execution);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = execution.Route.ModelId,
            Status = GetAgentContainerString(execution.Task, "status"),
            Output = new AIOutput
            {
                Items = content.Count == 0 ? null :
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = content,
                        Metadata = metadata
                    }
                ],
                Metadata = metadata
            },
            Usage = CreateAgentContainerUsage(execution.Task),
            Metadata = metadata
        };
    }

    private AIToolCallContentPart CreateAgentContainerIdentityToolPart(AgentContainerExecution execution)
    {
        var taskId = GetAgentContainerString(execution.Task, "id")!;
        var toolName = IdentityToolName(execution.Route);
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = BuildAgentContainerIdentityCallId(execution.Route, taskId),
            ToolName = toolName,
            Title = execution.Route.IsConversation ? "Create AgentContainer conversation" : "Dispatch AgentContainer task",
            Input = new { agentId = execution.Route.AgentId, mode = execution.Route.Mode },
            Output = CreateAgentContainerIdentityResult(execution),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = CreateAgentContainerMetadata(execution)
        };
    }

    private static AIToolCallContentPart CreateAgentContainerActivityPart(JsonElement activity)
    {
        var callId = GetAgentContainerString(activity, "callId") ?? GetAgentContainerString(activity, "id") ?? Guid.NewGuid().ToString("N");
        var tool = NormalizeAgentContainerToolName(GetAgentContainerString(activity, "toolKey") ?? "agentcontainer_tool");
        var failed = string.Equals(GetAgentContainerString(activity, "status"), "failed", StringComparison.OrdinalIgnoreCase);
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = callId,
            ToolName = tool,
            Title = GetAgentContainerString(activity, "toolKey") ?? tool,
            Input = TryGetAgentContainerProperty(activity, "input", out var input) ? input.Clone() : new { },
            Output = new CallToolResult { IsError = failed, StructuredContent = activity.Clone() },
            ProviderExecuted = true,
            State = failed ? "output-error" : "output-available",
            Metadata = new Dictionary<string, object?> { ["agentcontainer.raw"] = activity.Clone() }
        };
    }

    private IEnumerable<AIStreamEvent> CreateAgentContainerIdentityEvents(AgentContainerExecution execution, DateTimeOffset timestamp)
    {
        var taskId = GetAgentContainerString(execution.Task, "id")!;
        var callId = BuildAgentContainerIdentityCallId(execution.Route, taskId);
        var toolName = IdentityToolName(execution.Route);
        var scoped = CreateAgentContainerScopedMetadata(execution.Task);
        yield return CreateAgentContainerStreamEvent("tool-input-available", callId,
            new AIToolInputAvailableEventData
            {
                ToolName = toolName,
                Title = execution.Route.IsConversation ? "Create AgentContainer conversation" : "Dispatch AgentContainer task",
                Input = new { agentId = execution.Route.AgentId, mode = execution.Route.Mode },
                ProviderExecuted = true,
                ProviderMetadata = scoped
            }, timestamp, CreateAgentContainerMetadata(execution));
        yield return CreateAgentContainerStreamEvent("tool-output-available", callId,
            new AIToolOutputAvailableEventData
            {
                ToolName = toolName,
                Output = CreateAgentContainerIdentityResult(execution),
                ProviderExecuted = true,
                ProviderMetadata = scoped
            }, timestamp, CreateAgentContainerMetadata(execution));
    }

    private static CallToolResult CreateAgentContainerIdentityResult(AgentContainerExecution execution)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = IdentityToolName(execution.Route),
                taskId = GetAgentContainerString(execution.Task, "id"),
                task_id = GetAgentContainerString(execution.Task, "id"),
                conversationId = execution.Route.IsConversation ? GetAgentContainerString(execution.Task, "id") : null,
                conversation_id = execution.Route.IsConversation ? GetAgentContainerString(execution.Task, "id") : null,
                agentId = execution.Route.AgentId,
                agent_id = execution.Route.AgentId,
                mode = execution.Route.Mode,
                status = GetAgentContainerString(execution.Task, "status"),
                acceptedCommand = TryGetAgentContainerProperty(execution.Task, "acceptedCommand", out var accepted) ? accepted.Clone() : (JsonElement?)null,
                task = execution.Task.Clone()
            }, AgentContainerJson)
        };

    private static AIUsage? CreateAgentContainerUsage(JsonElement task)
    {
        if (!TryGetAgentContainerProperty(task, "tokenUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var input = GetAgentContainerInt(usage, "input_tokens") ?? GetAgentContainerInt(usage, "inputTokens");
        var output = GetAgentContainerInt(usage, "output_tokens") ?? GetAgentContainerInt(usage, "outputTokens");
        return new AIUsage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = input.HasValue || output.HasValue ? (input ?? 0) + (output ?? 0) : null,
            AdditionalProperties = new Dictionary<string, JsonElement> { ["agentcontainer"] = usage.Clone() }
        };
    }

    private static AIFinishEventData CreateAgentContainerFinishData(AgentContainerExecution execution, DateTimeOffset timestamp)
    {
        var usage = CreateAgentContainerUsage(execution.Task);
        var status = GetAgentContainerString(execution.Task, "status");
        return new AIFinishEventData
        {
            FinishReason = status is "awaiting_input" or "hibernating" ? "stop" : "stop",
            Model = execution.Route.ModelId,
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            InputTokens = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens,
            TotalTokens = usage?.TotalTokens,
            Response = execution.Task.Clone(),
            MessageMetadata = AIFinishMessageMetadata.Create(
                execution.Route.ModelId,
                timestamp,
                usage,
                inputTokens: usage?.InputTokens,
                outputTokens: usage?.OutputTokens,
                totalTokens: usage?.TotalTokens,
                gateway: new AIFinishGatewayMetadata { Cost = GetAgentContainerDecimal(execution.Task, "costUsd") },
                additionalProperties: new Dictionary<string, object?>
                {
                    ["agentcontainer"] = execution.Task.Clone()
                })
        };
    }

    private static Dictionary<string, object?> CreateAgentContainerMetadata(AgentContainerExecution execution)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["agentcontainer.task_id"] = GetAgentContainerString(execution.Task, "id"),
            ["agentcontainer.conversation_id"] = execution.Route.IsConversation ? GetAgentContainerString(execution.Task, "id") : null,
            ["agentcontainer.agent_id"] = execution.Route.AgentId,
            ["agentcontainer.mode"] = execution.Route.Mode,
            ["agentcontainer.status"] = GetAgentContainerString(execution.Task, "status"),
            ["agentcontainer.turn_number"] = execution.TurnNumber,
            ["agentcontainer.cost_usd"] = GetAgentContainerDecimal(execution.Task, "costUsd"),
            ["agentcontainer.container_minutes"] = GetAgentContainerDecimal(execution.Task, "containerMinutes"),
            ["agentcontainer.raw"] = execution.Task.Clone(),
            ["taskId"] = GetAgentContainerString(execution.Task, "id"),
            ["task_id"] = GetAgentContainerString(execution.Task, "id")
        };

    private AIStreamEvent CreateAgentContainerStreamEvent(
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };

    private static Dictionary<string, Dictionary<string, object>> CreateAgentContainerScopedMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["agentcontainer"] = new Dictionary<string, object> { ["raw"] = raw.Clone() }
        };

    private static Dictionary<string, object> CreateAgentContainerLooseMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase) { ["agentcontainer"] = new { raw = raw.Clone() } };

    private static string IdentityToolName(AgentContainerRoute route)
        => route.IsConversation ? ConversationIdentityTool : TaskIdentityTool;

    private static string BuildAgentContainerIdentityCallId(AgentContainerRoute route, string taskId)
        => $"agentcontainer-{route.Mode}-{taskId}";

    private static string NormalizeAgentContainerToolName(string value)
        => new string(value.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray()).Trim('_');
}
