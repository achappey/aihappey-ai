using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Hyperbrowser;

public partial class HyperbrowserProvider
{
    private static readonly HyperbrowserTaskDefinition HyperAgentTaskDefinition = new(
        Kind: "hyper-agent",
        Endpoint: "task/hyper-agent",
        ToolName: "hyperbrowser_hyper_agent_task",
        ToolTitle: "Hyperbrowser HyperAgent task",
        DisplayName: "HyperAgent",
        DefaultLlm: "gpt-4o",
        DefaultVersion: "0.8.0",
        SupportedLlms:
        [
            "gpt-5.2",
            "gpt-5.1",
            "gpt-5",
            "gpt-5-mini",
            "gpt-4o",
            "gpt-4o-mini",
            "gpt-4.1",
            "gpt-4.1-mini",
            "claude-sonnet-4-6",
            "claude-sonnet-4-5",
            "gemini-2.5-flash",
            "gemini-3-flash-preview"
        ],
        ApplySpecificPayload: ApplyHyperAgentPayload,
        CreateStepOutputItems: CreateHyperAgentStepOutputItems,
        CreateStepStreamEvents: CreateHyperAgentStepStreamEvents);

    private static void ApplyHyperAgentPayload(Dictionary<string, object?> payload, HyperbrowserTaskMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Version))
            payload["version"] = metadata.Version;
    }

    private static IEnumerable<AIOutputItem> CreateHyperAgentStepOutputItems(
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse task)
    {
        foreach (var step in EnumerateHyperAgentSteps(task))
        {
            if (step.AgentOutput.ValueKind != JsonValueKind.Object)
                continue;

            var thoughts = ExtractString(step.AgentOutput, "thoughts");
            if (!string.IsNullOrWhiteSpace(thoughts))
            {
                yield return new AIOutputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Content =
                    [
                        new AIReasoningContentPart
                        {
                            Type = "reasoning",
                            Text = thoughts,
                            Metadata = CreateHyperAgentStepReasoningMetadata(definition, created.JobId, step)
                        }
                    ]
                };
            }

            foreach (var action in EnumerateHyperAgentActions(step))
            {
                if (IsHyperAgentThinkAction(action))
                {
                    var actionThought = ExtractHyperAgentThinkActionThought(action);
                    if (!string.IsNullOrWhiteSpace(actionThought))
                    {
                        yield return new AIOutputItem
                        {
                            Type = "message",
                            Role = "assistant",
                            Content =
                            [
                                new AIReasoningContentPart
                                {
                                    Type = "reasoning",
                                    Text = actionThought,
                                    Metadata = CreateHyperAgentActionReasoningMetadata(definition, created.JobId, step, action)
                                }
                            ]
                        };
                    }

                    continue;
                }

                yield return new AIOutputItem
                {
                    Type = "tool-call",
                    Role = "assistant",
                    Content =
                    [
                        CreateHyperAgentStepToolCallPart(definition, created.JobId, step, action)
                    ]
                };
            }
        }
    }

    private static IEnumerable<AIStreamEvent> CreateHyperAgentStepStreamEvents(
        string providerId,
        string eventId,
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskResponse task,
        Dictionary<string, object?>? metadata,
        HashSet<int> emittedStepIndexes)
    {
        foreach (var step in EnumerateHyperAgentSteps(task))
        {
            if (!emittedStepIndexes.Add(step.Index) || step.AgentOutput.ValueKind != JsonValueKind.Object)
                continue;

            var timestamp = DateTimeOffset.UtcNow;
            var thoughts = ExtractString(step.AgentOutput, "thoughts");
            if (!string.IsNullOrWhiteSpace(thoughts))
            {
                var reasoningId = $"{eventId}_step_{step.Index}_reasoning";
                var providerMetadata = CreateHyperAgentStepReasoningProviderMetadata(providerId, definition, task.JobId, step);

                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    reasoningId,
                    "reasoning-start",
                    new AIReasoningStartEventData { ProviderMetadata = providerMetadata },
                    timestamp,
                    metadata);

                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    reasoningId,
                    "reasoning-delta",
                    new AIReasoningDeltaEventData { Delta = thoughts!, ProviderMetadata = providerMetadata },
                    timestamp,
                    metadata);

                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    reasoningId,
                    "reasoning-end",
                    new AIReasoningEndEventData { ProviderMetadata = providerMetadata },
                    timestamp,
                    metadata);
            }

            foreach (var action in EnumerateHyperAgentActions(step))
            {
                if (IsHyperAgentThinkAction(action))
                {
                    foreach (var reasoningEvent in CreateHyperAgentThinkActionStreamEvents(
                                 providerId,
                                 eventId,
                                 definition,
                                 task.JobId,
                                 step,
                                 action,
                                 timestamp,
                                 metadata))
                    {
                        yield return reasoningEvent;
                    }

                    continue;
                }

                var toolCallId = BuildHyperbrowserStepToolCallId(task.JobId, step.Index, action.Index, action.ToolName);
                var providerMetadata = CreateHyperAgentActionToolProviderMetadata(providerId, definition, toolCallId, step, action, "tool_use");

                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    toolCallId,
                    "tool-input-available",
                    new AIToolInputAvailableEventData
                    {
                        ToolName = action.ToolName,
                        Title = action.Description,
                        ProviderExecuted = true,
                        Input = action.Input,
                        ProviderMetadata = providerMetadata
                    },
                    timestamp,
                    metadata);

                if (action.Output is null)
                    continue;

                var isError = IsHyperAgentActionOutputError(action.Output.Value);
                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    toolCallId,
                    isError ? "tool-output-error" : "tool-output-available",
                    isError
                        ? new AIToolOutputErrorEventData
                        {
                            ToolCallId = toolCallId,
                            ErrorText = ExtractString(action.Output.Value, "message") ?? action.Output.Value.GetRawText(),
                            ProviderExecuted = true,
                            Dynamic = true,
                            ProviderMetadata = CreateHyperAgentActionToolProviderMetadata(providerId, definition, toolCallId, step, action, "tool_result")
                        }
                        : new AIToolOutputAvailableEventData
                        {
                            ToolName = action.ToolName,
                            Output = CreateHyperAgentActionToolResult(action.Output.Value),
                            ProviderExecuted = true,
                            Dynamic = true,
                            Preliminary = false,
                            ProviderMetadata = CreateHyperAgentActionToolProviderMetadata(providerId, definition, toolCallId, step, action, "tool_result")
                        },
                    timestamp,
                    metadata);
            }
        }
    }

    private static AIToolCallContentPart CreateHyperAgentStepToolCallPart(
        HyperbrowserTaskDefinition definition,
        string jobId,
        HyperAgentStepRecord step,
        HyperAgentActionRecord action)
        => new()
        {
            Type = "tool-call",
            ToolCallId = BuildHyperbrowserStepToolCallId(jobId, step.Index, action.Index, action.ToolName),
            ToolName = action.ToolName,
            Title = action.Description,
            Input = action.Input,
            Output = action.Output is null ? null : CreateHyperAgentActionToolResult(action.Output.Value),
            State = action.Output is null
                ? "input-available"
                : IsHyperAgentActionOutputError(action.Output.Value)
                    ? "output-error"
                    : "output-available",
            ProviderExecuted = true,
            Metadata = CreateHyperAgentStepToolMetadata(definition, jobId, step, action)
        };

    private static IEnumerable<AIStreamEvent> CreateHyperAgentThinkActionStreamEvents(
        string providerId,
        string eventId,
        HyperbrowserTaskDefinition definition,
        string jobId,
        HyperAgentStepRecord step,
        HyperAgentActionRecord action,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
    {
        var thought = ExtractHyperAgentThinkActionThought(action);
        if (string.IsNullOrWhiteSpace(thought))
            yield break;

        var reasoningId = $"{eventId}_step_{step.Index}_action_{action.Index}_reasoning";
        var providerMetadata = CreateHyperAgentActionReasoningProviderMetadata(providerId, definition, jobId, step, action);

        yield return CreateHyperbrowserStreamEvent(
            providerId,
            reasoningId,
            "reasoning-start",
            new AIReasoningStartEventData { ProviderMetadata = providerMetadata },
            timestamp,
            metadata);

        yield return CreateHyperbrowserStreamEvent(
            providerId,
            reasoningId,
            "reasoning-delta",
            new AIReasoningDeltaEventData { Delta = thought!, ProviderMetadata = providerMetadata },
            timestamp,
            metadata);

        yield return CreateHyperbrowserStreamEvent(
            providerId,
            reasoningId,
            "reasoning-end",
            new AIReasoningEndEventData { ProviderMetadata = providerMetadata },
            timestamp,
            metadata);
    }

    private static IEnumerable<HyperAgentStepRecord> EnumerateHyperAgentSteps(HyperbrowserTaskResponse task)
    {
        if (task.Data is null
            || task.Data.Value.ValueKind != JsonValueKind.Object
            || !task.Data.Value.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var fallbackIndex = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                fallbackIndex++;
                continue;
            }

            var index = TryGetInt(step, "idx") ?? fallbackIndex;
            var agentOutput = step.TryGetProperty("agentOutput", out var output) && output.ValueKind == JsonValueKind.Object
                ? output.Clone()
                : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), HyperbrowserJson);
            var actionOutputs = step.TryGetProperty("actionOutputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array
                ? outputs.Clone()
                : JsonSerializer.SerializeToElement(Array.Empty<object>(), HyperbrowserJson);

            yield return new HyperAgentStepRecord(index, fallbackIndex, step.Clone(), agentOutput, actionOutputs);
            fallbackIndex++;
        }
    }

    private static IEnumerable<HyperAgentActionRecord> EnumerateHyperAgentActions(HyperAgentStepRecord step)
    {
        var agentOutput = step.AgentOutput;
        if (agentOutput.ValueKind != JsonValueKind.Object
            || !agentOutput.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 0;
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            var toolName = ExtractString(action, "type") ?? "hyperbrowser_action";
            var input = action.TryGetProperty("params", out var parameters)
                ? parameters.Clone()
                : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), HyperbrowserJson);
            var output = TryGetHyperAgentActionOutput(step.ActionOutputs, index, out var actionOutput)
                ? actionOutput.Clone()
                : (JsonElement?)null;

            yield return new HyperAgentActionRecord(
                index,
                toolName,
                ExtractString(action, "actionDescription"),
                input,
                output,
                action.Clone());

            index++;
        }
    }

    private static bool TryGetHyperAgentActionOutput(JsonElement actionOutputs, int actionIndex, out JsonElement actionOutput)
    {
        actionOutput = default;

        if (actionOutputs.ValueKind != JsonValueKind.Array)
            return false;

        var index = 0;
        foreach (var output in actionOutputs.EnumerateArray())
        {
            if (index++ == actionIndex)
            {
                actionOutput = output;
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, object?> CreateHyperAgentStepReasoningMetadata(
        HyperbrowserTaskDefinition definition,
        string jobId,
        HyperAgentStepRecord step)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["hyperbrowser.agent_type"] = definition.Kind,
            ["hyperbrowser.job_id"] = jobId,
            ["hyperbrowser.step_idx"] = step.Index,
            ["hyperbrowser.step_order"] = step.Order,
            ["hyperbrowser.step.memory"] = ExtractString(step.AgentOutput, "memory"),
            ["hyperbrowser.step.next_goal"] = ExtractString(step.AgentOutput, "nextGoal"),
            ["hyperbrowser.step.raw"] = step.Raw
        };

        return metadata.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static Dictionary<string, object?> CreateHyperAgentStepToolMetadata(
        HyperbrowserTaskDefinition definition,
        string jobId,
        HyperAgentStepRecord step,
        HyperAgentActionRecord action)
        => new()
        {
            ["hyperbrowser.agent_type"] = definition.Kind,
            ["hyperbrowser.job_id"] = jobId,
            ["hyperbrowser.step_idx"] = step.Index,
            ["hyperbrowser.step_order"] = step.Order,
            ["hyperbrowser.action_index"] = action.Index,
            ["hyperbrowser.action_description"] = action.Description,
            ["hyperbrowser.action_raw"] = action.Raw
        };

    private static Dictionary<string, object?> CreateHyperAgentActionReasoningMetadata(
        HyperbrowserTaskDefinition definition,
        string jobId,
        HyperAgentStepRecord step,
        HyperAgentActionRecord action)
    {
        var metadata = CreateHyperAgentStepToolMetadata(definition, jobId, step, action);
        metadata["hyperbrowser.reasoning_source"] = "thinkAction";
        metadata["hyperbrowser.action_output"] = action.Output;
        return metadata.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static Dictionary<string, Dictionary<string, object>> CreateHyperAgentStepReasoningProviderMetadata(
        string providerId,
        HyperbrowserTaskDefinition definition,
        string jobId,
        HyperAgentStepRecord step)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "reasoning",
                ["agent_type"] = definition.Kind,
                ["job_id"] = jobId,
                ["step_idx"] = step.Index,
                ["step_order"] = step.Order,
                ["memory"] = ExtractString(step.AgentOutput, "memory") ?? string.Empty,
                ["next_goal"] = ExtractString(step.AgentOutput, "nextGoal") ?? string.Empty
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateHyperAgentActionReasoningProviderMetadata(
        string providerId,
        HyperbrowserTaskDefinition definition,
        string jobId,
        HyperAgentStepRecord step,
        HyperAgentActionRecord action)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "reasoning",
                ["reasoning_source"] = "thinkAction",
                ["agent_type"] = definition.Kind,
                ["job_id"] = jobId,
                ["step_idx"] = step.Index,
                ["step_order"] = step.Order,
                ["action_index"] = action.Index,
                ["title"] = action.Description ?? action.ToolName
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateHyperAgentActionToolProviderMetadata(
        string providerId,
        HyperbrowserTaskDefinition definition,
        string toolCallId,
        HyperAgentStepRecord step,
        HyperAgentActionRecord action,
        string type)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["agent_type"] = definition.Kind,
                ["tool_name"] = action.ToolName,
                ["title"] = action.Description ?? action.ToolName,
                ["tool_use_id"] = toolCallId,
                ["step_idx"] = step.Index,
                ["step_order"] = step.Order,
                ["action_index"] = action.Index
            }
        };

    private static CallToolResult CreateHyperAgentActionToolResult(JsonElement actionOutput)
    {
        var message = ExtractString(actionOutput, "message") ?? actionOutput.GetRawText();
        return new CallToolResult
        {
            Content = string.IsNullOrWhiteSpace(message) ? [] : [new TextContentBlock { Text = message }],
            StructuredContent = actionOutput.Clone()
        };
    }

    private static bool IsHyperAgentActionOutputError(JsonElement actionOutput)
        => actionOutput.ValueKind == JsonValueKind.Object
           && actionOutput.TryGetProperty("success", out var success)
           && success.ValueKind == JsonValueKind.False;

    private static bool IsHyperAgentThinkAction(HyperAgentActionRecord action)
        => string.Equals(action.ToolName, "thinkAction", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractHyperAgentThinkActionThought(HyperAgentActionRecord action)
    {
        if (action.Input.ValueKind == JsonValueKind.Object)
        {
            var thought = ExtractString(action.Input, "thought");
            if (!string.IsNullOrWhiteSpace(thought))
                return thought;
        }

        if (action.Output is { ValueKind: JsonValueKind.Object } output)
        {
            var message = ExtractString(output, "message");
            const string prefix = "A simple thought process about your next steps. You thought about:";
            if (!string.IsNullOrWhiteSpace(message))
                return message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? message[prefix.Length..].Trim()
                    : message;
        }

        return null;
    }

    private sealed record HyperAgentStepRecord(
        int Index,
        int Order,
        JsonElement Raw,
        JsonElement AgentOutput,
        JsonElement ActionOutputs);

    private sealed record HyperAgentActionRecord(
        int Index,
        string ToolName,
        string? Description,
        JsonElement Input,
        JsonElement? Output,
        JsonElement Raw);
}
