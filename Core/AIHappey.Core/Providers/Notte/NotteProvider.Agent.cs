using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Notte;

public partial class NotteProvider
{
    private async IAsyncEnumerable<AIContentPart?> RunAgent(Turn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        var task = Text(turn.Request);
        var body = turn.Options.DeepClone().AsObject();
        var sessionOptions = body["session"] is null ? new JsonObject() : Object(body["session"]);
        body.Remove("session");
        var explicitSession = body["session_id"]?.GetValue<string>();
        if (body.ContainsKey("session_id") && string.IsNullOrWhiteSpace(explicitSession)) throw new ArgumentException("Notte session_id must be non-empty.");
        turn.SessionId = explicitSession ?? FindSession(turn.Request);
        var wait = Control(turn.Controls, "wait_seconds", 300, 0.01, 86400);
        var poll = Control(turn.Controls, "poll_seconds", 2, 0.01, 30);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(wait));
        var token = deadline.Token;
        var finished = false;
        try
        {
            Reply? session = null;
            if (turn.SessionId is not null)
            {
                try { session = await Record(turn, HttpMethod.Get, $"sessions/{Enc(turn.SessionId)}", null, token); }
                catch (HttpRequestException error) when (explicitSession is null && error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                { turn.Warnings.Add("Replayed session is unavailable; created a replacement."); turn.SessionId = null; }
                if (session is not null && String(session.Raw, "status") is "closed" or "timed_out")
                {
                    if (explicitSession is not null) throw new InvalidOperationException("Explicit Notte session is no longer active.");
                    turn.Warnings.Add("Replayed session expired; created a replacement."); turn.SessionId = null;
                }
            }
            var created = turn.SessionId is null;
            if (created)
            {
                session = await Record(turn, HttpMethod.Post, "sessions/start", sessionOptions, token);
                turn.SessionId = String(session.Raw, "session_id") ?? throw new InvalidOperationException("Notte returned no session_id.");
            }
            var sessionStatus = String(session!.Raw, "status");
            if (sessionStatus == "authenticating")
            {
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(poll), token);
                    session = await Record(turn, HttpMethod.Get, $"sessions/{Enc(turn.SessionId!)}/auth", null, token);
                    sessionStatus = String(session.Raw, "status");
                } while (sessionStatus == "authenticating");
            }
            if (sessionStatus != "active") throw new InvalidOperationException($"Notte session is not ready: {session.Raw.GetRawText()}");
            if (created) yield return SessionPart(turn.SessionId!, sessionOptions, session.Raw);
            body["task"] = string.IsNullOrWhiteSpace(turn.Request.Instructions) ? task : turn.Request.Instructions + "\n\n" + task;
            body["session_id"] = turn.SessionId;
            var started = await Record(turn, HttpMethod.Post, "agents/start", body, token);
            turn.AgentId = String(started.Raw, "agent_id") ?? throw new InvalidOperationException("Notte returned no agent_id.");
            yield return null;
            Reply result;
            do
            {
                result = await Record(turn, HttpMethod.Get, $"agents/{Enc(turn.AgentId)}", null, token);
                yield return null;
                var status = String(result.Raw, "status");
                if (status == "closed") break;
                if (status != "active") throw new InvalidOperationException("Notte returned an unknown agent status.");
                await Task.Delay(TimeSpan.FromSeconds(poll), token);
                // Status snapshots replace the previous poll rather than growing history indefinitely.
                if (turn.Replies.Count > 2 && turn.Replies[^1] is not null) turn.Replies.RemoveAt(turn.Replies.Count - 1);
            } while (true);
            finished = true;
            turn.Status = Property(result.Raw, "success") is { ValueKind: JsonValueKind.False } ? "failed" : "completed";
            yield return TextPart(String(result.Raw, "answer") ?? result.Raw.GetRawText(), turn);
            await foreach (var file in DownloadFiles(turn, token)) yield return file;
        }
        finally
        {
            if (!finished && turn.AgentId is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await Send(turn.Request, HttpMethod.Delete, $"agents/{Enc(turn.AgentId)}/stop?session_id={Enc(turn.SessionId!)}", null, cleanup.Token); }
                catch { /* Best effort; preserve cancellation/original error. */ }
            }
        }
    }

    private static AIToolCallContentPart SessionPart(string id, JsonObject input, JsonElement raw)
    {
        var value = new { type = SessionTool, session_id = id, sessionId = id, raw };
        return new() { Type = "tool-call", ToolName = SessionTool, Title = "Create Notte session", ToolCallId = "notte-session-" + id,
            ProviderExecuted = true, State = "output-available", Input = input, Output = new CallToolResult
            { Content = [], StructuredContent = JsonSerializer.SerializeToElement(value, Json) },
            Metadata = new() { ["notte"] = value,
                ["messages.provider.call.metadata"] = new { notte = new { type = "server_tool_use", name = SessionTool, session_id = id } },
                ["messages.provider.result.metadata"] = new { notte = new { type = "mcp_tool_result", session_id = id } } } };
    }

    private static string? FindSession(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            foreach (var part in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
                if (part.ToolName == SessionTool || part.ProviderExecuted == true)
                {
                    var found = ExtractSession(part.Output, part.ToolName == SessionTool) ?? ExtractSession(part.Metadata, false)
                        ?? ExtractSession(part.Input, part.ToolName == SessionTool);
                    if (found is not null) return found;
                }
            var metadataSession = ExtractSession(item.Metadata, false);
            if (metadataSession is not null) return metadataSession;
        }
        return ExtractSession(request.Input?.Metadata, false);
    }

    private static string? ExtractSession(object? value, bool scoped, int depth = 0)
    {
        if (value is null || depth > 12) return null;
        var raw = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Json);
        if (raw.ValueKind == JsonValueKind.String)
        {
            try { using var doc = JsonDocument.Parse(raw.GetString()!); return ExtractSession(doc.RootElement, scoped, depth + 1); }
            catch (JsonException) { return null; }
        }
        if (raw.ValueKind != JsonValueKind.Object) return null;
        scoped |= String(raw, "type") == SessionTool || String(raw, "name") == SessionTool;
        if (scoped && String(raw, "session_id") is { Length: > 0 } id) return id;
        foreach (var property in raw.EnumerateObject())
            if (property.Name is "notte" or "structuredContent" or "output" or "provider_metadata" or "providerMetadata"
                or "chatcompletions.message.provider_metadata" or "messages.provider.metadata" or "messages.provider.call.metadata"
                or "messages.provider.result.metadata" or "messages.block.raw" or "content" or "input")
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var nested in property.Value.EnumerateArray().Reverse())
                        if (ExtractSession(nested, scoped, depth + 1) is { } found) return found;
                }
                else if (ExtractSession(property.Value, scoped || property.Name == "notte", depth + 1) is { } found) return found;
            }
        return null;
    }
}
