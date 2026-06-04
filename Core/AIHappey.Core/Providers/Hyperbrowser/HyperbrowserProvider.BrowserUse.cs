using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Hyperbrowser;

public partial class HyperbrowserProvider
{
    private static readonly HyperbrowserTaskDefinition BrowserUseTaskDefinition = new(
        Kind: "browser-use",
        Endpoint: "task/browser-use",
        ToolName: "hyperbrowser_browser_use_task",
        ToolTitle: "Hyperbrowser BrowserUse task",
        DisplayName: "BrowserUse",
        DefaultLlm: "gemini-2.0-flash",
        DefaultVersion: "0.1.40",
        SupportedLlms:
        [
            "gpt-4o",
            "gpt-4o-mini",
            "gpt-4.1",
            "gpt-4.1-mini",
            "claude-sonnet-4-6",
            "claude-sonnet-4-5",
            "claude-sonnet-4-20250514",
            "gemini-2.0-flash",
            "gemini-2.5-flash"
        ],
        ApplySpecificPayload: ApplyBrowserUsePayload,
        CreateStepOutputItems: CreateBrowserUseStepOutputItems,
        CreateStepStreamEvents: CreateBrowserUseStepStreamEvents);

    private static void ApplyBrowserUsePayload(Dictionary<string, object?> payload, HyperbrowserTaskMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Version))
            payload["version"] = metadata.Version;
        if (metadata.ValidateOutput is not null)
            payload["validateOutput"] = metadata.ValidateOutput;
        if (metadata.UseVision is not null)
            payload["useVision"] = metadata.UseVision;
        if (metadata.UseVisionForPlanner is not null)
            payload["useVisionForPlanner"] = metadata.UseVisionForPlanner;
        if (metadata.MaxActionsPerStep is not null)
            payload["maxActionsPerStep"] = metadata.MaxActionsPerStep;
        if (metadata.MaxInputTokens is not null)
            payload["maxInputTokens"] = metadata.MaxInputTokens;
        if (!string.IsNullOrWhiteSpace(metadata.PlannerLlm))
            payload["plannerLlm"] = metadata.PlannerLlm;
        if (!string.IsNullOrWhiteSpace(metadata.PageExtractionLlm))
            payload["pageExtractionLlm"] = metadata.PageExtractionLlm;
        if (metadata.PlannerInterval is not null)
            payload["plannerInterval"] = metadata.PlannerInterval;
        if (metadata.MaxFailures is not null)
            payload["maxFailures"] = metadata.MaxFailures;
        if (metadata.InitialActions is not null)
            payload["initialActions"] = metadata.InitialActions;
        if (metadata.SensitiveData is not null)
            payload["sensitiveData"] = metadata.SensitiveData;
        if (!string.IsNullOrWhiteSpace(metadata.MessageContext))
            payload["messageContext"] = metadata.MessageContext;
    }

    private static IEnumerable<AIOutputItem> CreateBrowserUseStepOutputItems(
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskStartResponse created,
        HyperbrowserTaskResponse task)
    {
        foreach (var step in EnumerateBrowserUseSteps(task))
        {
            var reasoning = CreateBrowserUseReasoningText(step.CurrentState);
            if (!string.IsNullOrWhiteSpace(reasoning))
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
                            Text = reasoning,
                            Metadata = CreateBrowserUseStepReasoningMetadata(definition, created.JobId, step)
                        }
                    ]
                };
            }

            foreach (var action in EnumerateBrowserUseActions(step))
            {
                yield return new AIOutputItem
                {
                    Type = "tool-call",
                    Role = "assistant",
                    Content = [CreateBrowserUseStepToolCallPart(definition, created.JobId, step, action)]
                };
            }

            var screenshot = CreateBrowserUseScreenshotFilePart(definition, created.JobId, step);
            if (screenshot is not null)
            {
                yield return new AIOutputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Content = [screenshot]
                };
            }
        }
    }

    private static IEnumerable<AIStreamEvent> CreateBrowserUseStepStreamEvents(
        string providerId,
        string eventId,
        HyperbrowserTaskDefinition definition,
        HyperbrowserTaskResponse task,
        Dictionary<string, object?>? metadata,
        HashSet<int> emittedStepIndexes)
    {
        foreach (var step in EnumerateBrowserUseSteps(task))
        {
            if (!emittedStepIndexes.Add(step.Index))
                continue;

            var timestamp = DateTimeOffset.UtcNow;
            var reasoning = CreateBrowserUseReasoningText(step.CurrentState);
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                var reasoningId = $"{eventId}_step_{step.Index}_reasoning";
                var providerMetadata = CreateBrowserUseStepReasoningProviderMetadata(providerId, definition, task.JobId, step);

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
                    new AIReasoningDeltaEventData { Delta = reasoning!, ProviderMetadata = providerMetadata },
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

            foreach (var action in EnumerateBrowserUseActions(step))
            {
                var toolCallId = BuildHyperbrowserStepToolCallId(task.JobId, step.Index, action.Index, action.ToolName);
                var providerMetadata = CreateBrowserUseActionToolProviderMetadata(providerId, definition, toolCallId, step, action, "tool_use");

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

                var isError = IsBrowserUseActionOutputError(action.Output.Value);
                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    toolCallId,
                    isError ? "tool-output-error" : "tool-output-available",
                    isError
                        ? new AIToolOutputErrorEventData
                        {
                            ToolCallId = toolCallId,
                            ErrorText = CreateBrowserUseActionOutputText(action.Output.Value),
                            ProviderExecuted = true,
                            Dynamic = true,
                            ProviderMetadata = CreateBrowserUseActionToolProviderMetadata(providerId, definition, toolCallId, step, action, "tool_result")
                        }
                        : new AIToolOutputAvailableEventData
                        {
                            ToolName = action.ToolName,
                            Output = CreateBrowserUseActionToolResult(action.Output.Value),
                            ProviderExecuted = true,
                            Dynamic = true,
                            Preliminary = false,
                            ProviderMetadata = CreateBrowserUseActionToolProviderMetadata(providerId, definition, toolCallId, step, action, "tool_result")
                        },
                    timestamp,
                    metadata);
            }

            var screenshot = ExtractBrowserUseScreenshotDataUrl(step);
            if (!string.IsNullOrWhiteSpace(screenshot))
            {
                yield return CreateHyperbrowserStreamEvent(
                    providerId,
                    $"{eventId}_step_{step.Index}_screenshot",
                    "file",
                    new AIFileEventData
                    {
                        MediaType = "image/png",
                        Url = screenshot,
                        Filename = CreateBrowserUseScreenshotFilename(step),
                        ProviderMetadata = CreateBrowserUseScreenshotProviderMetadata(providerId, definition, task.JobId, step)
                    },
                    timestamp,
                    metadata);
            }
        }
    }

    private static AIToolCallContentPart CreateBrowserUseStepToolCallPart(
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step,
        BrowserUseActionRecord action)
        => new()
        {
            Type = "tool-call",
            ToolCallId = BuildHyperbrowserStepToolCallId(jobId, step.Index, action.Index, action.ToolName),
            ToolName = action.ToolName,
            Title = action.Description,
            Input = action.Input,
            Output = action.Output is null ? null : CreateBrowserUseActionToolResult(action.Output.Value),
            State = action.Output is null
                ? "input-available"
                : IsBrowserUseActionOutputError(action.Output.Value)
                    ? "output-error"
                    : "output-available",
            ProviderExecuted = true,
            Metadata = CreateBrowserUseStepToolMetadata(definition, jobId, step, action)
        };

    private static IEnumerable<BrowserUseStepRecord> EnumerateBrowserUseSteps(HyperbrowserTaskResponse task)
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

            var metadata = step.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
                ? metadataElement.Clone()
                : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), HyperbrowserJson);
            var index = TryGetInt(metadata, "step_number") ?? TryGetInt(step, "idx") ?? fallbackIndex;
            var modelOutput = step.TryGetProperty("model_output", out var modelOutputElement) && modelOutputElement.ValueKind == JsonValueKind.Object
                ? modelOutputElement.Clone()
                : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), HyperbrowserJson);
            var currentState = modelOutput.TryGetProperty("current_state", out var currentStateElement) && currentStateElement.ValueKind == JsonValueKind.Object
                ? currentStateElement.Clone()
                : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), HyperbrowserJson);
            var state = step.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.Object
                ? stateElement.Clone()
                : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), HyperbrowserJson);
            var results = step.TryGetProperty("result", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
                ? resultsElement.Clone()
                : JsonSerializer.SerializeToElement(Array.Empty<object>(), HyperbrowserJson);

            yield return new BrowserUseStepRecord(index, fallbackIndex, step.Clone(), modelOutput, currentState, results, state, metadata);
            fallbackIndex++;
        }
    }

    private static IEnumerable<BrowserUseActionRecord> EnumerateBrowserUseActions(BrowserUseStepRecord step)
    {
        if (step.ModelOutput.ValueKind != JsonValueKind.Object
            || !step.ModelOutput.TryGetProperty("action", out var actions)
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

            var property = action.EnumerateObject().FirstOrDefault();
            var toolName = string.IsNullOrWhiteSpace(property.Name) ? "browser_use_action" : property.Name;
            var input = property.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), HyperbrowserJson)
                : property.Value.Clone();
            var output = TryGetBrowserUseActionResult(step.Results, index, out var actionResult)
                ? actionResult.Clone()
                : (JsonElement?)null;

            yield return new BrowserUseActionRecord(
                index,
                toolName,
                CreateBrowserUseActionDescription(toolName, input),
                input,
                output,
                action.Clone());

            index++;
        }
    }

    private static bool TryGetBrowserUseActionResult(JsonElement results, int actionIndex, out JsonElement actionResult)
    {
        actionResult = default;

        if (results.ValueKind != JsonValueKind.Array)
            return false;

        var index = 0;
        foreach (var result in results.EnumerateArray())
        {
            if (index++ == actionIndex)
            {
                actionResult = result;
                return true;
            }
        }

        return false;
    }

    private static string? CreateBrowserUseReasoningText(JsonElement currentState)
    {
        if (currentState.ValueKind != JsonValueKind.Object)
            return null;

        var lines = new List<string>();
        AddBrowserUseReasoningLine(lines, "Evaluation previous goal", ExtractString(currentState, "evaluation_previous_goal"));
        AddBrowserUseReasoningLine(lines, "Memory", ExtractString(currentState, "memory"));
        AddBrowserUseReasoningLine(lines, "Next goal", ExtractString(currentState, "next_goal"));
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static void AddBrowserUseReasoningLine(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            lines.Add($"{label}: {value}");
    }

    private static string CreateBrowserUseActionDescription(string toolName, JsonElement input)
    {
        var text = toolName.Replace('_', ' ');
        if (input.ValueKind != JsonValueKind.Object)
            return text;

        var goal = ExtractString(input, "goal");
        if (!string.IsNullOrWhiteSpace(goal))
            return $"{text}: {goal}";

        var url = ExtractString(input, "url");
        if (!string.IsNullOrWhiteSpace(url))
            return $"{text}: {url}";

        var index = TryGetInt(input, "index");
        return index is null ? text : $"{text}: element {index}";
    }

    private static AIFileContentPart? CreateBrowserUseScreenshotFilePart(
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step)
    {
        var screenshot = ExtractBrowserUseScreenshotDataUrl(step);
        return string.IsNullOrWhiteSpace(screenshot)
            ? null
            : new AIFileContentPart
            {
                Type = "file",
                MediaType = "image/png",
                Filename = CreateBrowserUseScreenshotFilename(step),
                Data = screenshot,
                Metadata = CreateBrowserUseScreenshotMetadata(definition, jobId, step)
            };
    }

    private static string? ExtractBrowserUseScreenshotDataUrl(BrowserUseStepRecord step)
    {
        var screenshot = ExtractString(step.State, "screenshot");
        if (string.IsNullOrWhiteSpace(screenshot))
            return null;

        return screenshot.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? screenshot
            : $"data:image/png;base64,{screenshot}";
    }

    private static string CreateBrowserUseScreenshotFilename(BrowserUseStepRecord step)
        => $"browseruse-step-{step.Index}.png";

    private static Dictionary<string, object?> CreateBrowserUseStepReasoningMetadata(
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step)
    {
        var metadata = CreateBrowserUseStepMetadata(definition, jobId, step);
        metadata["hyperbrowser.step.evaluation_previous_goal"] = ExtractString(step.CurrentState, "evaluation_previous_goal");
        metadata["hyperbrowser.step.memory"] = ExtractString(step.CurrentState, "memory");
        metadata["hyperbrowser.step.next_goal"] = ExtractString(step.CurrentState, "next_goal");
        return metadata.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static Dictionary<string, object?> CreateBrowserUseStepToolMetadata(
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step,
        BrowserUseActionRecord action)
    {
        var metadata = CreateBrowserUseStepMetadata(definition, jobId, step);
        metadata["hyperbrowser.action_index"] = action.Index;
        metadata["hyperbrowser.action_description"] = action.Description;
        metadata["hyperbrowser.action_raw"] = action.Raw;
        metadata["hyperbrowser.action_output"] = action.Output;
        return metadata.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static Dictionary<string, object?> CreateBrowserUseScreenshotMetadata(
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step)
    {
        var metadata = CreateBrowserUseStepMetadata(definition, jobId, step);
        metadata["hyperbrowser.file.kind"] = "browser_use_screenshot";
        metadata["hyperbrowser.file.media_type"] = "image/png";
        metadata["hyperbrowser.file.filename"] = CreateBrowserUseScreenshotFilename(step);
        return metadata.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static Dictionary<string, object?> CreateBrowserUseStepMetadata(
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step)
        => new()
        {
            ["hyperbrowser.agent_type"] = definition.Kind,
            ["hyperbrowser.job_id"] = jobId,
            ["hyperbrowser.step_idx"] = step.Index,
            ["hyperbrowser.step_order"] = step.Order,
            ["hyperbrowser.step.url"] = ExtractString(step.State, "url"),
            ["hyperbrowser.step.title"] = ExtractString(step.State, "title"),
            ["hyperbrowser.step_start_time"] = ExtractString(step.Metadata, "step_start_time"),
            ["hyperbrowser.step_end_time"] = ExtractString(step.Metadata, "step_end_time"),
            ["hyperbrowser.step.input_tokens"] = TryGetInt(step.Metadata, "input_tokens"),
            ["hyperbrowser.step.raw"] = step.Raw
        };

    private static Dictionary<string, Dictionary<string, object>> CreateBrowserUseStepReasoningProviderMetadata(
        string providerId,
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "reasoning",
                ["agent_type"] = definition.Kind,
                ["job_id"] = jobId,
                ["step_idx"] = step.Index,
                ["step_order"] = step.Order,
                ["url"] = ExtractString(step.State, "url") ?? string.Empty,
                ["title"] = ExtractString(step.State, "title") ?? string.Empty,
                ["memory"] = ExtractString(step.CurrentState, "memory") ?? string.Empty,
                ["next_goal"] = ExtractString(step.CurrentState, "next_goal") ?? string.Empty
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateBrowserUseActionToolProviderMetadata(
        string providerId,
        HyperbrowserTaskDefinition definition,
        string toolCallId,
        BrowserUseStepRecord step,
        BrowserUseActionRecord action,
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
                ["action_index"] = action.Index,
                ["url"] = ExtractString(step.State, "url") ?? string.Empty,
                ["page_title"] = ExtractString(step.State, "title") ?? string.Empty
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateBrowserUseScreenshotProviderMetadata(
        string providerId,
        HyperbrowserTaskDefinition definition,
        string jobId,
        BrowserUseStepRecord step)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "file",
                ["file_kind"] = "browser_use_screenshot",
                ["agent_type"] = definition.Kind,
                ["job_id"] = jobId,
                ["step_idx"] = step.Index,
                ["step_order"] = step.Order,
                ["url"] = ExtractString(step.State, "url") ?? string.Empty,
                ["page_title"] = ExtractString(step.State, "title") ?? string.Empty
            }
        };

    private static CallToolResult CreateBrowserUseActionToolResult(JsonElement actionOutput)
        => new()
        {
            Content = [new TextContentBlock { Text = CreateBrowserUseActionOutputText(actionOutput) }],
            StructuredContent = actionOutput.Clone()
        };

    private static string CreateBrowserUseActionOutputText(JsonElement actionOutput)
    {
        var extractedContent = ExtractString(actionOutput, "extracted_content");
        if (!string.IsNullOrWhiteSpace(extractedContent))
            return extractedContent!;

        var error = ExtractString(actionOutput, "error");
        if (!string.IsNullOrWhiteSpace(error))
            return error!;

        return actionOutput.GetRawText();
    }

    private static bool IsBrowserUseActionOutputError(JsonElement actionOutput)
        => actionOutput.ValueKind == JsonValueKind.Object
           && actionOutput.TryGetProperty("success", out var success)
           && success.ValueKind == JsonValueKind.False;

    private sealed record BrowserUseStepRecord(
        int Index,
        int Order,
        JsonElement Raw,
        JsonElement ModelOutput,
        JsonElement CurrentState,
        JsonElement Results,
        JsonElement State,
        JsonElement Metadata);

    private sealed record BrowserUseActionRecord(
        int Index,
        string ToolName,
        string? Description,
        JsonElement Input,
        JsonElement? Output,
        JsonElement Raw);
}
