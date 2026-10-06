using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.HCompany;

public partial class HCompanyProvider
{
    private AIStreamEvent Event(string type, string? id, object data, JsonElement raw) => new()
    {
        ProviderId = GetIdentifier(), Metadata = new() { ["hcompany.raw"] = raw.Clone() },
        Event = new AIEventEnvelope
        {
            Type = type, Id = id, Data = data,
            Timestamp = DateTimeOffset.TryParse(Text(raw, "timestamp"), out var time) ? time : DateTimeOffset.UtcNow
        }
    };

    private Dictionary<string, Dictionary<string, object>> ToolMetadata(JsonElement raw) => new()
        { [GetIdentifier()] = new() { ["raw"] = raw.Clone() } };

    private AIToolCallContentPart AddTool(AgentRun run, JsonElement call, bool providerExecuted)
    {
        var id = Text(call, "id") ?? throw new InvalidOperationException("HCompany tool request is missing its id.");
        if (run.Tools.TryGetValue(id, out var existing)) return existing;
        var tool = new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = id, ToolName = Text(call, "tool_name") ?? "unknown", Input = Property(call, "args") ?? Element(new { }),
            ProviderExecuted = providerExecuted, State = "input-available",
            Metadata = new() { ["hcompany"] = new { sessionId = run.Id, agentId = run.Agent, tool_req = call.Clone() }, ["hcompany.raw"] = call.Clone() }
        };
        run.Tools[id] = tool;
        run.Parts.Add(tool);
        return tool;
    }

    private IEnumerable<AIStreamEvent> ToolEvents(AIToolCallContentPart part, JsonElement raw)
    {
        yield return Event("tool-input-available", part.ToolCallId, new AIToolInputAvailableEventData
        {
            ToolName = part.ToolName ?? "unknown", Title = part.Title, Input = part.Input ?? Element(new { }),
            ProviderExecuted = part.ProviderExecuted, ProviderMetadata = ToolMetadata(raw)
        }, raw);
        if (part.State == "output-available")
            yield return Event("tool-output-available", part.ToolCallId, new AIToolOutputAvailableEventData
            {
                ToolName = part.ToolName, Output = part.Output ?? Element(null), ProviderExecuted = part.ProviderExecuted,
                ProviderMetadata = ToolMetadata(raw)
            }, raw);
    }

    private IEnumerable<AIStreamEvent> MapAgentEvent(AgentRun run, JsonElement evt)
    {
        // Keep open-ended event envelopes intact, including observation screenshots,
        // browser session IDs, downloaded-file descriptors, metrics, and future fields.
        yield return Event("data-hcompany", run.Id + "-event-" + run.Events.Count, evt.Clone(), evt);
        var data = Property(evt, "data") ?? default;
        if (Text(evt, "type") == "ActiveStateChangeEvent")
        {
            foreach (var call in run.Pending.Values)
                if (!run.Tools.ContainsKey(Text(call, "id")!))
                    foreach (var mapped in ToolEvents(AddTool(run, call, false), evt)) yield return mapped;
            yield break;
        }
        if (Text(evt, "type") == "MetricsUpdateEvent")
        {
            if (Property(data, "metrics") is { } metrics) run.Metrics = metrics;
            yield break;
        }
        if (Text(evt, "type") != "AgentEvent") yield break;
        switch (Text(data, "kind"))
        {
            case "policy_event":
                if (Text(data, "reasoning_content") is { Length: > 0 } reasoning)
                {
                    var id = run.Id + "-reasoning-" + run.Events.Count;
                    run.Parts.Add(new AIReasoningContentPart { 
                        Type = "reasoning",
                        Text = reasoning, Metadata = new() { ["hcompany.raw"] = evt.Clone() } });
                    yield return Event("reasoning-start", id, new AIReasoningStartEventData { ProviderMetadata = ToolMetadata(evt) }, evt);
                    yield return Event("reasoning-delta", id, new AIReasoningDeltaEventData { Delta = reasoning, ProviderMetadata = ToolMetadata(evt) }, evt);
                    yield return Event("reasoning-end", id, new AIReasoningEndEventData { ProviderMetadata = ToolMetadata(evt) }, evt);
                }
                foreach (var call in Array(data, "tool_reqs"))
                {
                    // Custom calls are declared by the caller and must not also be
                    // surfaced as provider-executed actions from the policy trace.
                    if (run.Pending.ContainsKey(Text(call, "id") ?? "")) continue;
                    if (!run.Tools.ContainsKey(Text(call, "id")!))
                        foreach (var mapped in ToolEvents(AddTool(run, call, true), evt)) yield return mapped;
                }
                break;
            case "tool_result":
                if (Property(data, "tool_req") is not { } req || Text(req, "id") is not { } toolId) break;
                var original = run.Tools.TryGetValue(toolId, out var saved) ? saved : AddTool(run, req, true);
                var result = new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = original.ToolCallId, ToolName = original.ToolName, Input = original.Input,
                    ProviderExecuted = original.ProviderExecuted, State = "output-available", Output = Property(data, "result"),
                    Metadata = new() { ["hcompany.raw"] = evt.Clone() }
                };
                var index = run.Parts.IndexOf(original);
                if (index >= 0) run.Parts[index] = result;
                run.Tools[toolId] = result;
                yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
                {
                    ToolName = result.ToolName, Output = result.Output ?? Element(null), ProviderExecuted = result.ProviderExecuted,
                    ProviderMetadata = ToolMetadata(evt)
                }, evt);
                break;
            case "answer_event":
                if (Property(data, "answer") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } answer) run.Answer = answer;
                break;
        }
    }

    private IEnumerable<AIStreamEvent> EmitAnswer(AgentRun run, JsonElement answer)
    {
        run.AnswerEmitted = true;
        var text = answer.ValueKind == JsonValueKind.String ? answer.GetString()! : answer.GetRawText();
        var raw = run.Session;
        run.Parts.Add(new AITextContentPart { Type = "text", Text = text, Metadata = new() { ["hcompany.raw"] = raw.Clone(), ["hcompany.answer"] = answer.Clone() } });
        var metadata = new Dictionary<string, object> { [GetIdentifier()] = new { sessionId = run.Id, agentId = run.Agent, raw, answer } };
        yield return Event("text-start", run.Id + "-answer", new AITextStartEventData { ProviderMetadata = metadata }, raw);
        yield return Event("text-delta", run.Id + "-answer", new AITextDeltaEventData { Delta = text, ProviderMetadata = metadata }, raw);
        yield return Event("text-end", run.Id + "-answer", new AITextEndEventData { ProviderMetadata = metadata }, raw);
    }
}
