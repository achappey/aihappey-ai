using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Plori;

public sealed partial class PloriProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = Agent(request);
        var action = Find(request, "action") ?? "invoke";
        if (action != "invoke") return await OperationAsync(request, agent, action, cancellationToken);

        var message = LatestMessage(request);
        var session = Find(request, "session_id", "sessionId", "thread_id");
        var idempotency = Find(request, "idempotency_key", "idempotencyKey");
        await using var client = await ConnectAsync(cancellationToken);
        var raw = await CallAsync(client, "invoke_agent", cancellationToken,
            (["agent_id", "agentId", "agent"], agent), (["message"], message),
            (["session_id", "sessionId"], session), (["idempotency_key", "idempotencyKey"], idempotency),
            (["wait"], true));

        var runId = Str(raw, "run_id", "runId") ?? DeepFind(raw, ["run_id", "runId"]);
        session = Str(raw, "session_id", "sessionId", "thread_id") ?? session;
        var status = Str(raw, "status") ?? "";
        // Each wait call can block up to 25s; do not submit again after a successful invoke.
        while (!IsTerminal(status) && !IsAwaiting(status) && string.IsNullOrWhiteSpace(Reply(raw)))
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new InvalidOperationException("Plori invoke_agent did not return a reply or a run ID for polling.");
            raw = await CallAsync(client, "get_run_result", cancellationToken,
                (["agent_id", "agentId", "agent"], agent), (["run_id", "runId"], runId), (["wait"], true));
            status = Str(raw, "status") ?? status;
            session = Str(raw, "session_id", "sessionId", "thread_id") ?? session;
        }
        return ToResponse(request, agent, raw, "invoke_agent", session, runId, message);
    }

    private static bool IsTerminal(string status) => status.ToLowerInvariant() is
        "completed" or "succeeded" or "success" or "failed" or "cancelled" or "canceled" or "stopped";
    private static bool IsAwaiting(string status) => status.ToLowerInvariant() is
        "awaiting_input" or "awaiting_approval" or "waiting_for_input" or "needs_input" or "pending_input";

    private static string? Reply(JsonElement raw)
    {
        foreach (var name in new[] { "reply", "response", "result", "output", "answer" })
        {
            var value = Prop(raw, name);
            if (value is { ValueKind: JsonValueKind.String } s) return s.GetString();
            if (value is { ValueKind: JsonValueKind.Object } nested)
                if (Str(nested, "text", "reply", "content") is { } text) return text;
        }
        return null;
    }

    private static string LatestMessage(AIRequest request)
    {
        var text = request.Input?.Items?.AsEnumerable().Reverse()
            .Where(i => string.Equals(i.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(i => string.Join("\n", i.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []))
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? request.Input?.Text;
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Plori requires a latest user text message.");
        return text;
    }

    private async Task<AIResponse> OperationAsync(AIRequest request, string agent, string action, CancellationToken ct)
    {
        var prefix = $"agents/{Uri.EscapeDataString(agent)}";
        var runId = Find(request, "run_id", "runId", "continuation_run_id");
        var session = Find(request, "session_id", "sessionId", "thread_id");
        JsonElement raw;
        string? message = null;
        switch (action)
        {
            case "list_agents": raw = await RestAsync(HttpMethod.Get, "agents", null, null, ct); break;
            case "get_agent": raw = await RestAsync(HttpMethod.Get, prefix, null, null, ct); break;
            case "list_runs": raw = await RestAsync(HttpMethod.Get, $"{prefix}/runs", null, null, ct); break;
            case "get_run":
            case "cancel_run":
                if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Plori run_id is required.");
                raw = await RestAsync(action == "get_run" ? HttpMethod.Get : HttpMethod.Post,
                    $"{prefix}/runs/{Uri.EscapeDataString(runId)}" + (action == "cancel_run" ? "/cancel" : ""), null, null, ct);
                break;
            case "submit_run":
                message = LatestMessage(request);
                var body = new Dictionary<string, object?> { ["message"] = message };
                if (session is not null) body["session_id"] = session;
                if (Find(request, "max_turn_tokens", "maxTurnTokens") is { } max && int.TryParse(max, out var tokens)) body["max_turn_tokens"] = tokens;
                raw = await RestAsync(HttpMethod.Post, $"{prefix}/runs", body,
                    Find(request, "idempotency_key", "idempotencyKey"), ct);
                runId = Str(raw, "run_id");
                session = Str(raw, "session_id") ?? session;
                break;
            default: throw new ArgumentException($"Unknown Plori action '{action}'.");
        }
        return ToResponse(request, agent, raw, action, session, runId, message);
    }

    private AIResponse ToResponse(AIRequest request, string agent, JsonElement raw, string action,
        string? session, string? runId, string? message)
    {
        runId ??= Str(raw, "run_id", "runId", "id");
        session ??= Str(raw, "session_id", "sessionId", "thread_id");
        var metadata = Meta(agent, raw, session, runId);
        var status = Str(raw, "status") ?? (action == "submit_run" ? "queued" : "completed");
        var identity = JsonSerializer.SerializeToElement(new
        {
            agent_id = agent, run_id = runId, session_id = session,
            continuation_run_id = Str(raw, "continuation_run_id"), status, action, raw = raw.Clone()
        }, Json);
        var parts = new List<AIContentPart>
        {
            new AIToolCallContentPart
            {
                Type = "tool-call",
                ToolCallId = $"plori-{runId ?? agent}-{action}", ToolName = IdentityTool,
                Title = $"Plori {action}", ProviderExecuted = true,
                Input = new { agent_id = agent, run_id = runId, session_id = session, message, action },
                Output = new CallToolResult { StructuredContent = identity },
                State = "output-available", Metadata = metadata
            }
        };
        // REST run receipts deliberately contain no reply. Never present receipts as assistant prose.
        var reply = action == "invoke_agent" ? Reply(raw) : null;
        if (!string.IsNullOrEmpty(reply))
            parts.Add(new AITextContentPart { Type = "text", Text = reply, Metadata = metadata });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = agent.ToModelId(GetIdentifier()), Status = status,
            Metadata = metadata, Usage = Prop(raw, "usage") ?? Prop(raw, "tokens"),
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = parts, Metadata = metadata }], Metadata = metadata }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Plori's MCP wait path is request/response, not token streaming. Emit a faithful
        // synthetic tool result followed by the complete reply once it is actually available.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var parts = response.Output?.Items?.SelectMany(i => i.Content ?? []) ?? [];
        foreach (var tool in parts.OfType<AIToolCallContentPart>())
        {
            yield return StreamEvent("tool-input-available", tool.ToolCallId,
                new AIToolInputAvailableEventData { ToolName = IdentityTool, Input = tool.Input!, ProviderExecuted = true }, response.Metadata);
            yield return StreamEvent("tool-output-available", tool.ToolCallId,
                new AIToolOutputAvailableEventData { ToolName = IdentityTool, Output = tool.Output!, ProviderExecuted = true }, response.Metadata);
        }
        foreach (var text in parts.OfType<AITextContentPart>())
        {
            var id = response.Model;
            yield return StreamEvent("text-start", id, new AITextStartEventData(), response.Metadata);
            yield return StreamEvent("text-delta", id, new AITextDeltaEventData { Delta = text.Text }, response.Metadata);
            yield return StreamEvent("text-end", id, new AITextEndEventData(), response.Metadata);
        }
        yield return StreamEvent("finish", response.Model, new AIFinishEventData
        {
            Model = response.Model,
            FinishReason = response.Status is "failed" or "cancelled" ? "error" : "stop",
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? "plori", DateTimeOffset.UtcNow,
                response.Usage, additionalProperties: new Dictionary<string, object?> { ["plori"] = response.Metadata ?? [] })
        }, response.Metadata);
    }

    private AIStreamEvent StreamEvent(string type, string? id, object data, Dictionary<string, object?>? metadata)
        => new() { ProviderId = GetIdentifier(), Event = new AIEventEnvelope
            { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow }, Metadata = metadata };
}
