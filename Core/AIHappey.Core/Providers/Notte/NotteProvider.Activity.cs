using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Notte;

public partial class NotteProvider
{
    // Notte returns cumulative step snapshots, not an append-only event stream. Keep
    // identity separate from payloads: results often enrich the original action.
    private sealed class AgentActivity
    {
        public HashSet<string> Reasoning { get; } = [];
        public HashSet<string> Results { get; } = [];
        public Dictionary<string, AgentAction> Actions { get; } = [];
    }

    private sealed record AgentAction(string Id, string Step, JsonElement Input, string Name, string Title);

    private static IEnumerable<AIContentPart> ReadAgentActivity(Turn turn, JsonElement snapshot)
    {
        if (Property(snapshot, "steps") is not { ValueKind: JsonValueKind.Array } steps) yield break;
        var activity = turn.Activity;
        var stepId = "initial";
        var position = 0;
        var occurrences = new Dictionary<string, int>();
        foreach (var step in steps.EnumerateArray())
        {
            var index = position++;
            var type = String(step, "type");
            if (type is null || Property(step, "value") is not { } value) continue;
            if (type == "agent_step_start")
            {
                stepId = Property(value, "step_number") is { ValueKind: JsonValueKind.Number } number
                    ? number.GetRawText() : String(value, "started_at") ?? index.ToString();
                continue;
            }

            // Browser observations are large page dumps, not human-facing progress.
            // The completion action repeats the final answer emitted by RunAgent.
            // Keep both in native status/debug metadata, but do not create visible
            // reasoning or tool cards (including result-only completion snapshots).
            if (type == "observation"
                || (type is "agent_completion" or "execution_result"
                    && String(Property(value, "action") ?? default, "type") == "completion")) continue;

            var identity = $"{stepId}:{type}:{String(value, "started_at") ?? index.ToString()}";
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            var key = $"{identity}:{occurrence}";
            var id = $"notte-agent-{turn.AgentId}-activity-{index}";
            var metadata = ActivityMetadata(turn, id, type, stepId, step);

            if (type == "agent_completion")
            {
                var fields = CompletionReasoning(Property(value, "state") ?? default);
                var text = new List<string>();
                foreach (var (label, content) in fields)
                    if (content is not null && activity.Reasoning.Add($"{key}:{label}:{content}"))
                        text.Add($"{label}: {content}");
                if (text.Count > 0)
                    yield return new AIReasoningContentPart { Type = "reasoning", Text = string.Join("\n\n", text), Metadata = metadata };
            }

            if (type == "agent_completion" && Property(value, "action") is { ValueKind: JsonValueKind.Object } input
                && String(input, "type") is { Length: > 0 } actionType && !activity.Actions.ContainsKey(key))
            {
                var action = new AgentAction(id + "-tool", stepId, input.Clone(), "notte_" + actionType,
                    "Notte: " + actionType.Replace('_', ' '));
                activity.Actions.Add(key, action);
                yield return ActionPart(action, "input-available", null, metadata);
            }
            else if (type == "execution_result" && value.ValueKind == JsonValueKind.Object && !activity.Results.Contains(key))
            {
                // An unfinished snapshot may acquire its result on a later poll.
                if (Property(value, "success") is not { ValueKind: JsonValueKind.True or JsonValueKind.False }) continue;
                if (Property(value, "action") is not { ValueKind: JsonValueKind.Object } executed
                    || String(executed, "type") is not { Length: > 0 } executedType) continue;
                var action = activity.Actions.Values.LastOrDefault(a => a.Step == stepId
                    && !activity.Results.Contains(a.Id) && SameAction(a.Input, executed));
                if (action is null)
                {
                    // A result-only snapshot is still useful, but must have an input
                    // before its output so consumers never receive an orphan result.
                    action = new AgentAction(id + "-tool", stepId, executed.Clone(), "notte_" + executedType,
                        "Notte: " + executedType.Replace('_', ' '));
                    activity.Actions.Add(key, action);
                    yield return ActionPart(action, "input-available", null, metadata);
                }
                activity.Results.Add(key);
                activity.Results.Add(action.Id);
                var failed = Property(value, "success") is { ValueKind: JsonValueKind.False };
                var error = TextValue(value, "exception_detail") ?? TextValue(value, "exception")
                    ?? TextValue(value, "message") ?? $"Notte action '{executedType}' failed.";
                if (failed) metadata["notte.tool.error"] = error;
                var output = new CallToolResult
                {
                    IsError = failed,
                    Content = TextValue(value, "message") is { } message
                        ? [new TextContentBlock { Text = message }] : [],
                    StructuredContent = value.Clone()
                };
                yield return ActionPart(action, failed ? "output-error" : "output-available", output, metadata);
            }
        }
    }

    private static (string Label, string? Text)[] CompletionReasoning(JsonElement state)
        => [("Previous action", TextValue(state, "previous_goal_eval")),
            ("Page summary", TextValue(state, "page_summary")), ("Memory", TextValue(state, "memory")),
            ("Next goal", TextValue(state, "next_goal"))];

    private static string? TextValue(JsonElement value, string name) => ReasoningText(String(value, name));

    private static string? ReasoningText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        // Do not show serialized JSON as reasoning, even if it is string-valued.
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try { using var document = JsonDocument.Parse(text); return null; }
            catch (JsonException) { /* Plain text/Markdown beginning with a bracket. */ }
        }
        return text;
    }

    private static bool SameAction(JsonElement planned, JsonElement executed)
        => String(planned, "type") == String(executed, "type")
            && new[] { "id", "url" }.All(name => String(planned, name) is not { } expected
                || String(executed, name) is not { } actual || expected == actual);

    private static Dictionary<string, object?> ActivityMetadata(Turn turn, string id, string type, string step, JsonElement raw)
        => new() { ["notte.activity.id"] = id,
            ["notte"] = new { session_id = turn.SessionId, agent_id = turn.AgentId, type, step_number = step, raw = raw.Clone() } };

    private static AIToolCallContentPart ActionPart(AgentAction action, string state, object? output, Dictionary<string, object?> metadata)
        => new() { Type = "tool-call", ToolCallId = action.Id, ToolName = action.Name, Title = action.Title,
            Input = action.Input, Output = output, ProviderExecuted = true, State = state, Metadata = metadata };

    private static void CollectPart(Turn turn, AIContentPart part)
    {
        if (part is AIToolCallContentPart tool)
        {
            var index = turn.Parts.FindIndex(p => p is AIToolCallContentPart previous && previous.ToolCallId == tool.ToolCallId);
            if (index >= 0) { turn.Parts[index] = tool; return; }
        }
        turn.Parts.Add(part);
    }
}
