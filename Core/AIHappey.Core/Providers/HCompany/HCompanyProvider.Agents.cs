using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.HCompany;

public partial class HCompanyProvider
{
    private const string SessionTool = "create_hcompany_agent_session";
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(30);
    private sealed class AgentRun
    {
        public required string Id { get; init; }
        public required string Agent { get; init; }
        public required string Model { get; init; }
        public JsonElement Session { get; set; }
        public JsonElement Status { get; set; }
        public JsonElement? Metrics { get; set; }
        public List<JsonElement> Events { get; } = [];
        public Dictionary<string, JsonElement> Pending { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AIToolCallContentPart> Tools { get; } = new(StringComparer.Ordinal);
        public List<AIContentPart> Parts { get; } = [];
        public int Cursor { get; set; }
        public bool Created { get; init; }
        public bool Submitted { get; set; }
        public bool FreshActivity { get; set; }
        public string? PreviousFinishedAt { get; set; }
        public JsonElement? Answer { get; set; }
        public bool AnswerEmitted { get; set; }
    }

    private static bool TryAgent(string? model, out string agent)
    {
        var local = model is null ? "" : LocalModel(model);
        agent = local.StartsWith("agent/", StringComparison.OrdinalIgnoreCase) ? local[6..] : "";
        return !string.IsNullOrWhiteSpace(agent);
    }

    private static string? FindSession(object? value, string agent, int depth = 0)
    {
        if (value is null || depth > 7) return null;
        var root = Element(value);
        if (root.ValueKind != JsonValueKind.Object) return null;
        var identity = Text(root, "agentId") ?? Text(root, "agent_id");
        if (identity is not null && identity != agent) return null;
        var id = Text(root, "session_id") ?? Text(root, "sessionId");
        if (!string.IsNullOrWhiteSpace(id)) return id;
        foreach (var name in new[] { "structuredContent", "hcompany", "metadata", "output" })
            if (Property(root, name) is { } nested && FindSession(nested, agent, depth + 1) is { } found) return found;
        return null;
    }

    private string? Continuation(AIRequest request, string agent)
    {
        var direct = FindSession(request.Metadata, agent) ?? FindSession(request.Input?.Metadata, agent);
        if (direct is not null) return direct;
        foreach (var tool in (request.Input?.Items ?? []).SelectMany(i => i.Content ?? [])
                     .OfType<AIToolCallContentPart>().Reverse())
            if (tool.ProviderExecuted == true && tool.ToolName == SessionTool)
            {
                var id = FindSession(tool.Output, agent) ?? FindSession(tool.Metadata, agent);
                if (id is not null) return id;
            }
        return null;
    }

    private async Task<AgentRun> StartAgentAsync(AIRequest request, CancellationToken ct)
    {
        if (!TryAgent(request.Model, out var agent)) throw new ArgumentException("Invalid HCompany agent model.");
        var options = ProviderOptions(request.Metadata);
        var existing = Continuation(request, agent);
        JsonElement session;
        if (existing is null)
        {
            var messages = UserMessages(request, false);
            if (messages.Count == 0) throw new ArgumentException("HCompany agents require a user instruction.");
            var body = options.Where(p => p.Key is not ("session_id" or "sessionId" or "headers" or "idempotency_key" or "tools" or "providerMetadata"))
                .ToDictionary(p => p.Key, p => (object?)p.Value);
            body["agent"] = agent;
            body["messages"] = messages;
            var overrides = body.TryGetValue("overrides", out var supplied) && Element(supplied).ValueKind == JsonValueKind.Object
                ? Element(supplied).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()) : new Dictionary<string, object?>();
            if (request.Tools?.Count > 0)
                overrides["agent.tools"] = request.Tools.Select(tool => new
                {
                    name = tool.Name, description = tool.Description,
                    input_schema = tool.InputSchema ?? Element(new { type = "object", properties = new { } })
                }).ToArray();
            if (AnswerSchema(Element(request.ResponseFormat)) is { } schema && !overrides.ContainsKey("agent.answer_format"))
                overrides["agent.answer_format"] = schema;
            if (!string.IsNullOrWhiteSpace(request.Instructions) && !overrides.ContainsKey("agent.instructions"))
                overrides["agent.instructions"] = request.Instructions;
            if (overrides.Count > 0) body["overrides"] = overrides;
            var headers = RequestHeaders(request);
            if (options.TryGetValue("idempotency_key", out var key) && key.ValueKind == JsonValueKind.String)
                headers["Idempotency-Key"] = key.GetString()!;
            session = await SendJsonAsync(_agents, HttpMethod.Post, "sessions", body, ct, headers);
        }
        else session = await SendJsonAsync(_agents, HttpMethod.Get, SessionPath(existing), null, ct);
        var id = Text(session, "id") ?? throw new InvalidOperationException("HCompany session is missing its id.");
        if (existing is not null)
        {
            var spec = Property(Property(session, "request") ?? default, "agent") ?? default;
            var name = spec.ValueKind == JsonValueKind.String ? spec.GetString() : Text(spec, "name");
            if (name != agent) throw new InvalidOperationException("HCompany session belongs to a different agent.");
        }
        var run = new AgentRun
        {
            Id = id, Agent = agent, Model = request.Model!.ToModelId(GetIdentifier()), Session = session,
            Status = Property(session, "status") ?? default, Created = existing is null,
            PreviousFinishedAt = Text(session, "finished_at")
        };
        if (run.Created)
        {
            run.Parts.Add(SessionPart(run));
            return run;
        }

        // Drain persisted events before sending new input. This gives a current cursor
        // and the authoritative original tool_req, without replaying old answers/actions.
        while (true)
        {
            var changes = await ChangesAsync(run, 0, ct);
            var events = Array(changes, "new_events").ToList();
            foreach (var evt in events) CapturePending(run, evt);
            run.Cursor += events.Count;
            if (events.Count == 0) break;
        }
        var current = await SendJsonAsync(_agents, HttpMethod.Get, SessionPath(id) + "/status", null, ct);
        run.Status = current;
        if (Text(current, "status") != "awaiting_tool_results") run.Pending.Clear();
        CapturePendingStatus(run, current);
        var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var part in (request.Input?.Items ?? []).SelectMany(i => i.Content ?? []).OfType<AIToolCallContentPart>())
        {
            if (part.ProviderExecuted == true || !run.Pending.TryGetValue(part.ToolCallId, out var original)
                || !(part.State is "output-available" or "output-error" or "output-denied" || part.Output is not null || part.Approval?.Approved == false)) continue;
            var failure = part.State is "output-error" or "output-denied" || part.Approval?.Approved == false;
            var output = Element(part.Output);
            failure |= Property(output, "isError")?.ValueKind == JsonValueKind.True || Property(output, "is_error")?.ValueKind == JsonValueKind.True;
            var result = failure
                ? Element(new { kind = "error_event", error = part.Approval?.Reason ?? part.Output?.ToString() ?? "Client tool failed or was denied.", origin = "custom_tools", tool_req = original })
                : Element(new { kind = "tool_result", tool_req = original, result = part.Output });
            if (results.TryGetValue(part.ToolCallId, out var previous) && previous.GetRawText() != result.GetRawText())
                throw new ArgumentException($"Conflicting HCompany custom-tool results for {part.ToolCallId}.");
            results[part.ToolCallId] = result;
        }
        if (results.Count > 0)
        {
            await SendJsonAsync(_agents, HttpMethod.Post, SessionPath(id) + "/tool_results",
                new { type = "batch", results = results.Values }, ct, RequestHeaders(request));
            foreach (var resultId in results.Keys) run.Pending.Remove(resultId);
            run.Submitted = true;
        }
        var followup = UserMessages(request, true);
        if (followup.Count > 0)
        {
            await SendJsonAsync(_agents, HttpMethod.Post, SessionPath(id) + "/messages",
                new { type = "batch", messages = followup }, ct, RequestHeaders(request));
            run.Submitted = true;
        }
        // With a partial batch, return the remaining calls instead of waiting forever.
        if (run.Pending.Count > 0 && Text(run.Status, "status") == "awaiting_tool_results")
            foreach (var call in run.Pending.Values) AddTool(run, call, false);
        return run;
    }

    private Dictionary<string, string> RequestHeaders(AIRequest request)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ProviderOptions(request.Metadata).TryGetValue("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            foreach (var header in headers.EnumerateObject()) if (header.Value.ValueKind == JsonValueKind.String) result[header.Name] = header.Value.GetString()!;
        foreach (var header in request.Headers ?? []) result[header.Key] = header.Value;
        return result;
    }

    private static List<object> UserMessages(AIRequest request, bool continuation)
    {
        var items = request.Input?.Items ?? [];
        var boundary = continuation ? items.FindLastIndex(i => !string.Equals(i.Role, "user", StringComparison.OrdinalIgnoreCase)) : -1;
        var messages = new List<object>();
        foreach (var item in items.Skip(boundary + 1))
        {
            if (item.Role is not ("user" or "system" or "developer")) continue;
            var text = string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>().Select(p => p.Text));
            var images = (item.Content ?? []).OfType<AIFileContentPart>().Where(p => p.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                .Select(p => ImageData(p)).ToArray();
            if (!string.IsNullOrWhiteSpace(text) || images.Length > 0)
                messages.Add(new { type = "user_message", message = text, images });
        }
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
            messages.Add(new { type = "user_message", message = request.Input.Text });
        if (messages.Count == 0 && !continuation && !string.IsNullOrWhiteSpace(request.Instructions))
            messages.Add(new { type = "user_message", message = request.Instructions });
        return messages;
    }

    private static string ImageData(AIFileContentPart part)
    {
        var value = part.Data is byte[] bytes ? Convert.ToBase64String(bytes) : part.Data?.ToString() ?? "";
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return value;
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new NotSupportedException("HCompany agents require inline base64 image data, not remote image URLs.");
        try { Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new ArgumentException("HCompany agents require base64 image data.", ex); }
        return $"data:{part.MediaType};base64,{value}";
    }

    private static string SessionPath(string id) => "sessions/" + Uri.EscapeDataString(id);
    private Task<JsonElement> ChangesAsync(AgentRun run, int wait, CancellationToken ct)
        => SendJsonAsync(_agents, HttpMethod.Get, SessionPath(run.Id) + $"/changes?from_index={run.Cursor}&limit=200&include_events=true&wait_for_seconds={wait}", null, ct);

    private static void CapturePendingStatus(AgentRun run, JsonElement value)
    {
        if (Property(value, "pending_tool_calls") is not { ValueKind: JsonValueKind.Array }) return;
        run.Pending.Clear();
        foreach (var call in Array(value, "pending_tool_calls"))
            if (Text(call, "id") is { } id) run.Pending[id] = call.Clone();
    }

    private static void CapturePending(AgentRun run, JsonElement evt)
    {
        if (Text(evt, "type") != "ActiveStateChangeEvent") return;
        var data = Property(evt, "data") ?? default;
        run.Pending.Clear();
        if (Text(data, "state") == "awaiting_tool_results") CapturePendingStatus(run, data);
    }

    private async IAsyncEnumerable<AIStreamEvent> FollowAgentAsync(AgentRun run,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AgentTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var changes = await ChangesAsync(run, Settled(run) ? 0 : 25, timeout.Token);
            if (Property(changes, "metrics") is { } metrics) run.Metrics = metrics;
            var events = Array(changes, "new_events").ToList();
            run.Cursor += events.Count;
            foreach (var evt in events)
            {
                run.Events.Add(evt.Clone());
                run.FreshActivity = true;
                CapturePending(run, evt);
                foreach (var mapped in MapAgentEvent(run, evt)) yield return mapped;
            }
            run.Status = await SendJsonAsync(_agents, HttpMethod.Get, SessionPath(run.Id) + "/status", null, timeout.Token);
            CapturePendingStatus(run, run.Status);
            var status = Text(run.Status, "status");
            if (status is "running" or "pending" or "queued") run.FreshActivity = true;
            if (events.Count > 0 || !Settled(run)) continue;
            run.Session = await SendJsonAsync(_agents, HttpMethod.Get, SessionPath(run.Id), null, timeout.Token);
            // Send-messages is asynchronous. An unchanged old terminal snapshot must
            // never satisfy a new turn before the restarted run has become visible.
            if (run.Submitted && !run.FreshActivity && Text(run.Session, "finished_at") == run.PreviousFinishedAt)
            {
                await Task.Delay(250, timeout.Token);
                continue;
            }
            if (status == "awaiting_tool_results")
            {
                if (run.Pending.Count == 0) throw new InvalidOperationException("HCompany awaits tool results but supplied no pending calls.");
                foreach (var call in run.Pending.Values)
                    if (!run.Tools.ContainsKey(Text(call, "id")!))
                    {
                        var part = AddTool(run, call, false);
                        foreach (var mapped in ToolEvents(part, call)) yield return mapped;
                    }
            }
            else
            {
                run.Pending.Clear();
                var answer = Property(run.Session, "latest_answer");
                if (answer is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }) run.Answer = answer;
                if (!run.AnswerEmitted && run.Answer is { } final)
                    foreach (var mapped in EmitAnswer(run, final)) yield return mapped;
            }
            yield break;
        }
    }

    private static bool Settled(AgentRun run) => Text(run.Status, "status") is
        "completed" or "failed" or "timed_out" or "interrupted" or "awaiting_tool_results" or "idle";

    private async Task<AIResponse> ExecuteAgentAsync(AIRequest request, CancellationToken ct)
    {
        var run = await StartAgentAsync(request, ct);
        if (!(run.Pending.Count > 0 && run.Submitted))
            await foreach (var _ in FollowAgentAsync(run, ct)) { }
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = run.Model, Status = ResponseStatus(run),
            Output = new AIOutput { Items = run.Parts.Select(p => new AIOutputItem { Role = "assistant", Content = [p] }).ToList() },
            Usage = Usage(run), Metadata = RunMetadata(run)
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var run = await StartAgentAsync(request, ct);
        if (run.Created)
            foreach (var evt in ToolEvents(run.Parts.OfType<AIToolCallContentPart>().First(), run.Session)) yield return evt;
        if (run.Pending.Count > 0 && run.Submitted)
        {
            foreach (var call in run.Pending.Values)
                foreach (var evt in ToolEvents(run.Tools[Text(call, "id")!], call)) yield return evt;
        }
        else await foreach (var evt in FollowAgentAsync(run, ct)) yield return evt;
        var usage = Usage(run);
        if (ResponseStatus(run) == "failed")
            yield return Event("error", run.Id, new AIErrorEventData { ErrorText = Text(run.Status, "error") ?? $"HCompany session {Text(run.Status, "status")}." }, run.Status);
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(), Metadata = RunMetadata(run),
            Event = new AIEventEnvelope
            {
                Type = "finish", Id = run.Id, Timestamp = DateTimeOffset.UtcNow,
                Data = new AIFinishEventData
                {
                    FinishReason = ResponseStatus(run) == "requires_action" ? "tool-calls" : ResponseStatus(run) == "failed" ? "error" : "stop",
                    Model = run.Model, InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens,
                    MessageMetadata = AIFinishMessageMetadata.Create(run.Model, DateTimeOffset.UtcNow, usage,
                        inputTokens: usage.InputTokens, outputTokens: usage.OutputTokens, totalTokens: usage.TotalTokens,
                        reasoningTokens: usage.ReasoningTokens, additionalProperties: new() { ["hcompany"] = RunMetadata(run) })
                }
            }
        };
    }

    private static string ResponseStatus(AgentRun run) => Text(run.Status, "status") == "awaiting_tool_results" ? "requires_action"
        : Text(run.Status, "status") is "failed" or "timed_out" or "interrupted" ? "failed" : "completed";

    private static AIUsage Usage(AgentRun run)
    {
        var perModel = Array(run.Status, "usage_per_model").ToList();
        if (perModel.Count == 0 && run.Metrics.HasValue) perModel = Array(run.Metrics.Value, "cost_per_model").ToList();
        var input = perModel.Sum(p => Int(p, "input_tokens"));
        var output = perModel.Sum(p => Int(p, "output_tokens"));
        return new AIUsage { InputTokens = input, OutputTokens = output, TotalTokens = input + output, ReasoningTokens = perModel.Sum(p => Int(p, "reasoning_tokens")) };
    }

    private Dictionary<string, object?> RunMetadata(AgentRun run) => new()
    {
        ["hcompany"] = new { sessionId = run.Id, session_id = run.Id, agentId = run.Agent, from_index = run.Cursor,
            raw = run.Session, status = run.Status, metrics = run.Metrics, events = run.Events, pending_tool_calls = run.Pending.Values.ToArray() },
        ["hcompany.sessionId"] = run.Id, ["hcompany.raw"] = run.Session
    };

    private AIToolCallContentPart SessionPart(AgentRun run) => new()
    {
        Type = "tool-call",
        ToolCallId = "hcompany-create-session-" + run.Id, ToolName = SessionTool, Title = "Create HCompany agent session",
        Input = new { agent = run.Agent }, ProviderExecuted = true, State = "output-available",
        Output = new CallToolResult { Content = [], StructuredContent = Element(new { sessionId = run.Id, agentId = run.Agent, raw = run.Session }) },
        Metadata = RunMetadata(run)
    };
}
