using System.Net;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Connect0;

public sealed partial class Connect0Provider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseRoute(request.Model);
        var options = Options(request);
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("Connect0 agents execute their tools at the provider. Gateway client tool definitions cannot be forwarded.");

        Reply reply;
        string operation;
        string? runId = null;
        string? threadId = null;
        switch (route.Kind)
        {
            case RouteKind.ListAgents:
                operation = "list_agents";
                reply = await SendJson(HttpMethod.Get, $"v1/accounts/{Enc(route.Account)}/agents", null, cancellationToken);
                break;
            case RouteKind.AgentDetail:
                operation = "get_agent";
                reply = await SendJson(HttpMethod.Get, PublicAgentPath(route), null, cancellationToken);
                break;
            case RouteKind.Agent:
                operation = "create_run";
                reply = await CreateRun(route, request, options, cancellationToken);
                (runId, threadId) = RunIdentity(reply.Raw);
                if (runId is null) throw new InvalidOperationException("Connect0 queued a run without an id; cannot poll or resume it.");
                reply = await WaitForRun(route, runId, reply, options, cancellationToken);
                threadId = String(reply.Raw, "thread_id") ?? threadId;
                break;
            case RouteKind.ListRuns:
                operation = "list_runs";
                reply = await SendJson(HttpMethod.Get, PublicAgentPath(route) + "/runs" + Query(options, "limit", "cursor", "status"), null, cancellationToken);
                break;
            case RouteKind.RunDetail:
                operation = "get_run";
                runId = RequiredId(request, options, route, "run_id");
                reply = await GetRun(route, runId, cancellationToken);
                threadId = String(reply.Raw, "thread_id");
                break;
            case RouteKind.CancelRun:
                operation = "cancel_run";
                runId = RequiredId(request, options, route, "run_id");
                reply = await SendJson(HttpMethod.Post, PublicAgentPath(route) + $"/runs/{Enc(runId)}/cancel", null, cancellationToken);
                threadId = PreviousIdentity(request, route, "thread_id");
                break;
            case RouteKind.ReplayRun:
                operation = "replay_run";
                runId = RequiredId(request, options, route, "run_id");
                var mode = Option(options, "mode") ?? "in_place";
                if (mode is not ("in_place" or "fork")) throw new ArgumentException("Connect0 replay mode must be in_place or fork.");
                var replayBody = new Dictionary<string, object?> { ["mode"] = mode };
                if (Option(options, "fork_at_message_id", "forkAtMessageId") is { } fork)
                {
                    if (!Guid.TryParse(fork, out _)) throw new ArgumentException("Connect0 fork_at_message_id must be a UUID.");
                    replayBody["fork_at_message_id"] = fork;
                }
                if (Property(options, "input_override") is { ValueKind: JsonValueKind.Object } overrideInput)
                    replayBody["input_override"] = overrideInput;
                var agentId = await AgentId(route, cancellationToken);
                reply = await SendJson(HttpMethod.Post,
                    $"v1/accounts/{Enc(route.Account)}/agents/{Enc(agentId)}/runs/{Enc(runId)}/replay", replayBody, cancellationToken);
                (runId, threadId) = RunIdentity(reply.Raw);
                if (runId is null) throw new InvalidOperationException("Connect0 replay returned no run id.");
                reply = await WaitForRun(route, runId, reply, options, cancellationToken);
                threadId = String(reply.Raw, "thread_id") ?? threadId;
                break;
            case RouteKind.ListThreads:
                operation = "list_threads";
                reply = await SendJson(HttpMethod.Get, PublicAgentPath(route) + "/threads" + Query(options, "limit"), null, cancellationToken);
                break;
            case RouteKind.Messages:
                operation = "get_messages";
                threadId = RequiredId(request, options, route, "thread_id");
                reply = await SendJson(HttpMethod.Get, PublicAgentPath(route) + $"/threads/{Enc(threadId)}/messages", null, cancellationToken);
                break;
            case RouteKind.StreamRun:
                throw new NotSupportedException("Connect0 run event streams require a streaming gateway request.");
            default: throw new ArgumentOutOfRangeException(nameof(request));
        }
        return ToResponse(route, operation, reply, runId, threadId);
    }

    private async Task<Reply> CreateRun(Route route, AIRequest request, JsonElement options, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["input"] = Input(request, options) };
        var thread = Option(options, "thread_id", "threadId") ?? PreviousIdentity(request, route, "thread_id");
        if (thread is not null)
        {
            if (!Guid.TryParse(thread, out _)) throw new ArgumentException("Connect0 thread_id must be a UUID.");
            body["thread_id"] = thread;
        }
        if (Option(options, "thread_title", "threadTitle") is { } title)
        {
            if (title.Length > 200) throw new ArgumentException("Connect0 thread_title exceeds 200 characters.");
            body["thread_title"] = title;
        }
        if (Option(options, "project_id", "projectId") is { } project)
        {
            if (!Guid.TryParse(project, out _)) throw new ArgumentException("Connect0 project_id must be a UUID.");
            body["project_id"] = project;
        }
        if (Option(options, "trigger_kind", "triggerKind") is { } trigger)
        {
            if (trigger is not ("manual" or "rest" or "cron" or "webhook" or "mcp" or "agent" or "telegram"
                or "email" or "slack" or "a2a" or "text" or "pubsub"))
                throw new ArgumentException("Unsupported Connect0 trigger_kind.");
            body["trigger_kind"] = trigger;
        }
        return await SendJson(HttpMethod.Post, PublicAgentPath(route) + "/runs", body, ct);
    }

    private Task<Reply> GetRun(Route route, string runId, CancellationToken ct)
        => SendJson(HttpMethod.Get, PublicAgentPath(route) + $"/runs/{Enc(runId)}", null, ct);

    private static (string? RunId, string? ThreadId) RunIdentity(JsonElement raw)
        => (String(raw, "id") ?? String(raw, "run_id"), String(raw, "thread_id"));

    private static bool IsEndState(string? status) => status is
        "succeeded" or "failed" or "timeout" or "budget_blocked" or "cancelled" or "awaiting_input";

    private static TimeSpan PollTimeout(JsonElement options)
    {
        var timeout = Property(options, "wait_seconds");
        if (timeout is { ValueKind: JsonValueKind.Number } numeric && numeric.TryGetInt32(out var seconds))
        {
            if (seconds is < 1 or > 300) throw new ArgumentException("Connect0 wait_seconds must be between 1 and 300.");
            return TimeSpan.FromSeconds(seconds);
        }
        return DefaultPollTimeout;
    }

    private async Task<Reply> WaitForRun(Route route, string runId, Reply queued, JsonElement options, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout(options);
        var last = queued;
        while (!IsEndState(String(last.Raw, "status")) && DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var delay = DefaultPollInterval;
            if (last.Headers.TryGetValue("Retry-After", out var retry) && int.TryParse(retry?.ToString(), out var seconds))
                delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));
            await Task.Delay(delay < remaining ? delay : remaining, ct);
            if (DateTimeOffset.UtcNow >= deadline) break;
            try { last = await GetRun(route, runId, ct); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = error.Data["retryAfter"] as TimeSpan? ?? DefaultPollInterval;
                var left = deadline - DateTimeOffset.UtcNow;
                if (left > TimeSpan.Zero) await Task.Delay(retryAfter < left ? retryAfter : left, ct);
            }
        }
        if (!IsEndState(String(last.Raw, "status")))
            last.Headers["connect0.poll_timed_out"] = true;
        return last;
    }

    private AIResponse ToResponse(Route route, string operation, Reply reply, string? runId, string? threadId)
    {
        var raw = reply.Raw;
        runId ??= operation is "create_run" or "get_run" or "replay_run" ? String(raw, "id") : null;
        threadId ??= String(raw, "thread_id");
        var metadata = Metadata(route, reply, runId, threadId);
        var status = String(raw, "status") ?? (operation == "cancel_run" ? "cancelled" : "completed");
        var parts = new List<AIContentPart> { Receipt(route, reply, operation, runId, threadId) };
        var text = operation is "create_run" or "get_run" or "replay_run" ? RunText(raw) : null;
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = route.Model, Status = status,
            Metadata = metadata,
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = parts, Metadata = metadata }], Metadata = metadata }
        };
    }

    private static string? RunText(JsonElement raw)
    {
        if (Property(raw, "output") is not { } output) return null;
        if (output.ValueKind == JsonValueKind.String) return output.GetString();
        return String(output, "text") ?? String(output, "reply") ?? String(output, "message") ?? String(output, "answer");
    }
}
