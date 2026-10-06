using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Codebase;

public partial class CodebaseProvider
{
    private sealed class Turn(Prepared prepared)
    {
        public Prepared Prepared { get; } = prepared;
        public State State { get; set; } = prepared.Previous!;
        public Reply? Accepted { get; set; }
        public Reply? Status { get; set; }
        public bool TimedOut { get; set; }
        public string CallId { get; } = "codebase-build-" + Guid.NewGuid().ToString("N");
        public object Receipt => new { type = ReceiptType, provider = "codebase", projectId = State.ProjectId,
            sessionId = State.SessionId, model = Prepared.Model, operation = Prepared.Operation,
            idempotencyKey = Prepared.Key, accepted = Accepted?.Raw, status = Status?.Raw,
            responseHeaders = Accepted?.Headers, timedOut = TimedOut };
        public Dictionary<string, object?> Metadata => new() { ["codebase"] = Receipt };
        public string ResponseStatus => TimedOut ? "incomplete" : Text(Status?.Raw ?? default, "status") switch
        { "completed" => "completed", "cancelled" => "cancelled", "failed" or "rejected" => "failed", _ => "incomplete" };
    }

    private async Task<Turn> StartAsync(Prepared prepared, CancellationToken ct)
    {
        var turn = new Turn(prepared);
        if (prepared.Operation == "status") return turn;
        if (prepared.Operation == "retry")
        {
            // Validate both identifiers against authoritative state before mutating a session.
            var before = await StatusAsync(turn.State.SessionId, ct);
            ValidateState(turn.State, before.Raw);
            turn.Accepted = await SendJsonAsync(HttpMethod.Post, SessionPath(turn.State.SessionId) + "/retry",
                prepared.Body, prepared.Headers, prepared.Key, ct);
            // The documented retry response has no body; retain the known identifiers.
            if (Text(turn.Accepted.Raw, "sessionId") is { } id && id != turn.State.SessionId)
                turn.State = new(turn.State.ProjectId, id);
        }
        else
        {
            turn.Accepted = await SendJsonAsync(HttpMethod.Post, "api/v1/builds", prepared.Body,
                prepared.Headers, prepared.Key, ct);
            var project = Text(turn.Accepted.Raw, "projectId");
            var session = Text(turn.Accepted.Raw, "sessionId");
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(session))
                throw new InvalidOperationException("Codebase build acceptance is missing projectId or sessionId.");
            if (prepared.Previous is { } previous && project != previous.ProjectId)
                throw new InvalidOperationException("Codebase did not continue the requested durable project.");
            turn.State = new(project, session);
        }
        return turn;
    }

    private static void ValidateState(State state, JsonElement raw)
    {
        if (Text(raw, "sessionId") != state.SessionId || Text(raw, "projectId") != state.ProjectId)
            throw new InvalidOperationException("Codebase status does not match the executor receipt's projectId and sessionId.");
    }

    private async Task FollowAsync(Turn turn, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(turn.Prepared.WaitSeconds));
        var trace = _debug.Enabled ? TraceEventsAsync(turn.State.SessionId, lifetime) : Task.CompletedTask;
        var timer = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                turn.Status = await StatusAsync(turn.State.SessionId, deadline.Token);
                ValidateState(turn.State, turn.Status.Raw);
                var status = Text(turn.Status.Raw, "status");
                if (status is "completed" or "failed" or "cancelled" or "rejected" or "idle") return;
                if (status != "building") throw new InvalidOperationException("Unknown Codebase build status: " + status);
                if (timer.Elapsed.TotalSeconds >= turn.Prepared.WaitSeconds) { turn.TimedOut = true; return; }
                await Task.Delay(TimeSpan.FromSeconds(turn.Prepared.PollSeconds), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { turn.TimedOut = true; }
        finally
        {
            await lifetime.CancelAsync();
            await trace;
        }
    }

    private static AIToolCallContentPart Executor(Turn turn) => new()
    {
        Type = "tool-call", ToolCallId = turn.CallId, ToolName = ExecutorTool, Title = "Codebase build",
        Input = new { operation = turn.Prepared.Operation, prompt = turn.Prepared.Body.GetValueOrDefault("prompt"),
            projectId = turn.State.ProjectId, sessionId = turn.State.SessionId, model = turn.Prepared.Model },
        Output = new CallToolResult { Content = [], StructuredContent = Element(turn.Receipt) },
        ProviderExecuted = true, State = "output-available", Metadata = turn.Metadata
    };

    private static string Answer(Turn turn)
    {
        var raw = turn.Status?.Raw ?? default;
        var text = new StringBuilder();
        if (turn.TimedOut) text.AppendLine("Codebase build is still running or its final state could not be read before the gateway deadline. Preserve the executor receipt to check status without starting another build.");
        else if (Text(raw, "status") == "completed") text.AppendLine(Text(raw, "summary") ?? "Codebase build completed.");
        else
        {
            text.AppendLine("Codebase build status: " + (Text(raw, "status") ?? "unknown") + ".");
            if (Property(raw, "failure") is { } failure && Text(failure, "message") is { } message) text.AppendLine(message);
            else if (Text(raw, "error") is { } error) text.AppendLine(error);
            if (Property(raw, "interrupted")?.ValueKind == JsonValueKind.True) text.AppendLine("The build was interrupted by a provider server restart.");
        }
        foreach (var (field, title) in new[] { ("previewUrl", "Preview"), ("workspaceUrl", "Workspace"), ("sourceUrl", "Source ZIP (API key required)") })
        {
            var link = Text(raw, field) ?? Text(turn.Accepted?.Raw ?? default, field);
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                text.AppendLine($"[{title}](<{uri.AbsoluteUri.Replace(">", "%3E")}>)");
        }
        return text.ToString().TrimEnd();
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = await StartAsync(Prepare(request), cancellationToken);
        await FollowAsync(turn, cancellationToken);
        var metadata = turn.Metadata;
        metadata["chatcompletions.response.raw"] = Element(new { provider_metadata = new { codebase = turn.Receipt } });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = turn.Prepared.Model, Status = turn.ResponseStatus, Metadata = metadata,
            Output = new() { Items = [new() { Role = "assistant", Content = [Executor(turn),
                new AITextContentPart { Type = "text", Text = Answer(turn), Metadata = turn.Metadata }],
                Metadata = new() { ["codebase"] = turn.Receipt,
                    ["chatcompletions.choice.finish_reason"] = turn.ResponseStatus is "failed" or "cancelled" ? "error" : turn.ResponseStatus == "incomplete" ? "length" : "stop",
                    ["chatcompletions.message.provider_metadata"] = new { codebase = turn.Receipt } } }], Metadata = metadata }
        };
    }

    private static Dictionary<string, Dictionary<string, object>> Scoped(Turn turn) => new() { ["codebase"] = new() { ["receipt"] = turn.Receipt } };
    private static AIStreamEvent Event(string type, string id, object data, Turn turn) => new()
    { ProviderId = "codebase", Metadata = turn.Metadata, Event = new() { Type = type, Id = id,
        Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = turn.Metadata } };

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = await StartAsync(Prepare(request), cancellationToken);
        var tool = Executor(turn);
        yield return Event("tool-input-start", turn.CallId, new AIToolInputStartEventData
        { ToolName = ExecutorTool, Title = tool.Title, ProviderExecuted = true, ProviderMetadata = Scoped(turn) }, turn);
        yield return Event("tool-input-available", turn.CallId, new AIToolInputAvailableEventData
        { ToolName = ExecutorTool, Title = tool.Title, Input = tool.Input!, ProviderExecuted = true, ProviderMetadata = Scoped(turn) }, turn);
        // This receipt is an acceptance/recovery artifact, not a claim that the build succeeded.
        yield return Event("tool-output-available", turn.CallId, new AIToolOutputAvailableEventData
        { ToolName = ExecutorTool, Output = tool.Output!, ProviderExecuted = true, Preliminary = true, ProviderMetadata = Scoped(turn) }, turn);
        await FollowAsync(turn, cancellationToken);
        yield return Event("tool-output-available", turn.CallId, new AIToolOutputAvailableEventData
        { ToolName = ExecutorTool, Output = Executor(turn).Output!, ProviderExecuted = true, Preliminary = false, ProviderMetadata = Scoped(turn) }, turn);
        var textId = turn.CallId + "-summary";
        var loose = new Dictionary<string, object> { ["codebase"] = turn.Receipt };
        yield return Event("text-start", textId, new AITextStartEventData { ProviderMetadata = loose }, turn);
        yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = Answer(turn), ProviderMetadata = loose }, turn);
        yield return Event("text-end", textId, new AITextEndEventData { ProviderMetadata = loose }, turn);
        if (turn.ResponseStatus is "failed" or "cancelled")
            yield return Event("error", turn.CallId, new AIErrorEventData { ErrorText = Answer(turn) }, turn);
        var now = DateTimeOffset.UtcNow;
        var metadata = turn.Metadata;
        metadata["chatcompletions.stream.raw"] = Element(new { provider_metadata = new { codebase = turn.Receipt } });
        yield return new AIStreamEvent { ProviderId = "codebase", Metadata = metadata, Event = new() { Type = "finish", Id = turn.CallId,
            Timestamp = now, Data = new AIFinishEventData { Model = turn.Prepared.Model,
                FinishReason = turn.ResponseStatus is "failed" or "cancelled" ? "error" : turn.ResponseStatus == "incomplete" ? "length" : "stop",
                CompletedAt = now.ToUnixTimeSeconds(), MessageMetadata = AIFinishMessageMetadata.Create(turn.Prepared.Model, now, additionalProperties: turn.Metadata) } } };
    }
}
