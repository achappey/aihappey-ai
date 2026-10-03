using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Skyvern;

public partial class SkyvernProvider
{
    private sealed class Turn
    {
        public required Transport Transport { get; init; }
        public required string Model { get; init; }
        public required string Operation { get; init; }
        public required JsonObject Controls { get; init; }
        public required JsonObject Body { get; init; }
        public required List<Attachment> Attachments { get; init; }
        public required double WaitSeconds { get; init; }
        public required double PollSeconds { get; init; }
        public string ToolId { get; } = $"skyvern-{Guid.NewGuid():N}";
        public string ToolName => $"skyvern_{Operation}";
        public string? RunId { get; set; }
        public bool Owned { get; set; }
        public bool WaitTimedOut { get; set; }
        public Reply Reply { get; set; } = new(JsonSerializer.SerializeToElement(new { }), []);
        public List<JsonElement> Uploads { get; } = [];
        public JsonElement? Artifacts { get; set; }
        public List<AIFileContentPart> Files { get; } = [];
        public List<string> Warnings { get; } = [];
    }

    private Turn Prepare(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("Skyvern saved agents execute their configured workflow tools; gateway client tool definitions are not supported.");
        var id = AgentId(request.Model);
        var options = Options(request);
        var controls = options["gateway"] is null ? new JsonObject()
            : options["gateway"] as JsonObject ?? throw new ArgumentException("Skyvern gateway controls must be an object.");
        options.Remove("gateway");
        var operation = controls["operation"]?.GetValue<string>() ?? "run";
        if (operation is not ("run" or "get" or "wait" or "cancel" or "upload" or "artifacts" or "artifact_content"))
            throw new ArgumentException("Unknown Skyvern gateway operation.");
        var runId = controls["run_id"]?.GetValue<string>();
        if (operation is "get" or "wait" or "cancel" or "artifacts" or "artifact_content" && string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("This Skyvern operation requires gateway.run_id.");
        var wait = NumberControl(controls, "wait_seconds", 300, 0, 28800);
        var poll = NumberControl(controls, "poll_seconds", 2, 0.01, 30);
        if (controls["retention_days"] is { } retention && retention.GetValue<int>() < 1)
            throw new ArgumentException("Skyvern retention_days must be positive.");
        // These two fields belong exclusively to the gateway's selected model and user input.
        options.Remove("workflow_id");
        options.Remove("agent_id");
        options.Remove("parameters");
        if (operation == "run")
        {
            options["agent_id"] = id;
            options["parameters"] = JsonNode.Parse(Parameters(request).GetRawText());
        }
        var attachments = operation is "run" or "upload" ? Attachments(request) : [];
        var configuredIds = FileIds(options);
        if (configuredIds.Count + attachments.Count > 50)
            throw new ArgumentException("Skyvern accepts at most 50 attached file IDs per run.");
        if (operation == "upload" && attachments.Count == 0)
            throw new ArgumentException("Skyvern upload requires at least one binary file attachment in the latest user message.");
        if (operation == "artifact_content" && string.IsNullOrWhiteSpace(controls["artifact_id"]?.GetValue<string>()))
            throw new ArgumentException("Skyvern artifact_content requires gateway.artifact_id.");
        return new Turn
        {
            Transport = GetTransport(request), Model = $"skyvern/{id}", Operation = operation,
            Controls = controls, Body = options, Attachments = attachments,
            RunId = runId, WaitSeconds = wait, PollSeconds = poll
        };
    }

    private static double NumberControl(JsonObject controls, string name, double fallback, double min, double max)
    {
        var value = controls[name]?.GetValue<double>() ?? fallback;
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ArgumentException($"Skyvern gateway.{name} must be between {min} and {max}.");
        return value;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = Prepare(request);
        await foreach (var _ in Run(turn, cancellationToken)) { }
        await CompleteFiles(turn, cancellationToken);
        return ToResponse(turn);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = Prepare(request);
        var inputSent = false;
        await foreach (var reply in Run(turn, cancellationToken))
        {
            var metadata = Metadata(turn);
            var providerMetadata = ProviderMetadata(turn);
            if (!inputSent)
            {
                inputSent = true;
                yield return Event(turn, "tool-input-start", new AIToolInputStartEventData
                {
                    ToolName = turn.ToolName, Title = "Skyvern " + turn.Operation,
                    ProviderExecuted = true, ProviderMetadata = providerMetadata
                }, metadata);
                yield return Event(turn, "tool-input-available", new AIToolInputAvailableEventData
                {
                    ToolName = turn.ToolName, Title = "Skyvern " + turn.Operation, Input = ToolInput(turn),
                    ProviderExecuted = true, ProviderMetadata = providerMetadata
                }, metadata);
            }
            // Progress is actual provider state, not inferred reasoning or fabricated actions.
            if (IsActive(reply.Raw))
                yield return Event(turn, "tool-output-available", new AIToolOutputAvailableEventData
                {
                    ToolName = turn.ToolName, Output = ToolResult(turn), ProviderExecuted = true,
                    Preliminary = true, Dynamic = true, ProviderMetadata = providerMetadata
                }, metadata);
        }
        await CompleteFiles(turn, cancellationToken);
        var finalMetadata = Metadata(turn);
        var failed = Failed(turn.Reply.Raw);
        if (failed)
            yield return Event(turn, "tool-output-error", new AIToolOutputErrorEventData
            {
                ToolCallId = turn.ToolId, ErrorText = ErrorText(turn.Reply.Raw), ProviderExecuted = true,
                Dynamic = true, ProviderMetadata = ProviderMetadata(turn)
            }, finalMetadata);
        else
            yield return Event(turn, "tool-output-available", new AIToolOutputAvailableEventData
            {
                ToolName = turn.ToolName, Output = ToolResult(turn), ProviderExecuted = true,
                Preliminary = false, Dynamic = true, ProviderMetadata = ProviderMetadata(turn)
            }, finalMetadata);
        var text = ResultText(turn);
        if (!string.IsNullOrEmpty(text))
        {
            var textMetadata = new Dictionary<string, object> { ["skyvern"] = MetadataValue(turn) };
            yield return Event(turn, "text-start", new AITextStartEventData { ProviderMetadata = textMetadata }, finalMetadata, turn.ToolId + "-text");
            yield return Event(turn, "text-delta", new AITextDeltaEventData { Delta = text, ProviderMetadata = textMetadata }, finalMetadata, turn.ToolId + "-text");
            yield return Event(turn, "text-end", new AITextEndEventData { ProviderMetadata = textMetadata }, finalMetadata, turn.ToolId + "-text");
        }
        for (var index = 0; index < turn.Files.Count; index++)
        {
            var file = turn.Files[index];
            yield return Event(turn, "file", new AIFileEventData
            {
                Filename = file.Filename, MediaType = file.MediaType ?? "application/octet-stream",
                Url = $"data:{file.MediaType ?? "application/octet-stream"};base64,{file.Data}",
                ProviderMetadata = new() { ["skyvern"] = new() { ["raw"] = file.Metadata ?? [] } }
            }, finalMetadata, turn.ToolId + "-file-" + index);
        }
        var now = DateTimeOffset.UtcNow;
        yield return Event(turn, "finish", new AIFinishEventData
        {
            Model = turn.Model, FinishReason = failed ? "error" : IsActive(turn.Reply.Raw) || String(turn.Reply.Raw, "status") == "paused" ? "other" : "stop",
            CompletedAt = now.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(turn.Model, now,
                additionalProperties: new() { ["skyvern"] = MetadataValue(turn) })
        }, finalMetadata);
    }

    private async IAsyncEnumerable<Reply> Run(Turn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        var handedOff = false;
        try
        {
            switch (turn.Operation)
            {
                case "run":
                    var ids = FileIds(turn.Body);
                    foreach (var file in turn.Attachments)
                    {
                        var uploaded = await Upload(turn, file, ct);
                        turn.Uploads.Add(uploaded.Raw);
                        ids.Add(String(uploaded.Raw, "file_id") ?? throw new InvalidOperationException("Skyvern upload returned no file_id."));
                    }
                    if (ids.Count > 0) turn.Body["file_ids"] = JsonSerializer.SerializeToNode(ids.Distinct().ToArray(), Json);
                    var path = "v1/run/agents";
                    // template is an API query parameter, not a request-body property.
                    if (turn.Body["template"] is { } template)
                    {
                        path += "?template=" + template.GetValue<bool>().ToString().ToLowerInvariant();
                        turn.Body.Remove("template");
                    }
                    turn.Reply = await SendJson(turn.Transport, HttpMethod.Post, path, turn.Body, ct);
                    turn.RunId = String(turn.Reply.Raw, "run_id")
                        ?? throw new InvalidOperationException("Skyvern started a run without returning run_id.");
                    turn.Owned = true;
                    break;
                case "get":
                case "wait":
                    turn.Reply = await GetRun(turn, ct);
                    break;
                case "cancel":
                    var canceled = await SendJson(turn.Transport, HttpMethod.Post, $"v1/runs/{Enc(turn.RunId!)}/cancel", null, ct);
                    // Cancellation accepts an empty response. Read the run to preserve the
                    // provider's actual status instead of claiming cancellation is complete.
                    turn.Reply = await GetRun(turn, ct);
                    turn.Reply.Headers["skyvern.cancel_headers"] = canceled.Headers;
                    break;
                case "upload":
                    foreach (var file in turn.Attachments)
                    {
                        var uploaded = await Upload(turn, file, ct);
                        turn.Uploads.Add(uploaded.Raw);
                        turn.Reply = uploaded;
                    }
                    turn.Reply = new Reply(JsonSerializer.SerializeToElement(new { uploads = turn.Uploads }, Json), turn.Reply.Headers);
                    break;
                case "artifacts":
                case "artifact_content":
                    turn.Reply = await ListArtifacts(turn, ct);
                    turn.Artifacts = turn.Reply.Raw;
                    break;
            }
            yield return turn.Reply;
            if (turn.Operation is "run" or "wait")
            {
                var elapsed = Stopwatch.StartNew();
                var previous = turn.Reply.Raw.GetRawText();
                while (IsActive(turn.Reply.Raw) && elapsed.Elapsed.TotalSeconds < turn.WaitSeconds)
                {
                    var remaining = turn.WaitSeconds - elapsed.Elapsed.TotalSeconds;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(turn.PollSeconds, remaining)), ct);
                    if (elapsed.Elapsed.TotalSeconds >= turn.WaitSeconds) break;
                    try { turn.Reply = await GetRun(turn, ct); }
                    catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        remaining = turn.WaitSeconds - elapsed.Elapsed.TotalSeconds;
                        if (remaining > 0)
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(((TimeSpan)error.Data["retryAfter"]!).TotalSeconds, remaining)), ct);
                        continue;
                    }
                    var raw = turn.Reply.Raw.GetRawText();
                    if (raw != previous)
                    {
                        previous = raw;
                        yield return turn.Reply;
                    }
                }
                turn.WaitTimedOut = IsActive(turn.Reply.Raw);
            }
            handedOff = true;
        }
        finally
        {
            // Independent, bounded token: the gateway's request token is already canceled.
            if (!handedOff && turn.Owned && turn.RunId is not null && IsActive(turn.Reply.Raw))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await SendJson(turn.Transport, HttpMethod.Post, $"v1/runs/{Enc(turn.RunId)}/cancel", null, cleanup.Token); }
                catch { /* Best effort; do not replace the original exception. */ }
            }
        }
    }

    private Task<Reply> GetRun(Turn turn, CancellationToken ct)
        => SendJson(turn.Transport, HttpMethod.Get, $"v1/runs/{Enc(turn.RunId!)}", null, ct);

    private static bool IsActive(JsonElement raw)
        => Flag(raw, "retry_pending") || String(raw, "status") is "created" or "queued" or "running";
    private static bool Failed(JsonElement raw)
        => !Flag(raw, "retry_pending") && String(raw, "status") is "failed" or "terminated" or "timed_out" or "canceled";
    private static string ErrorText(JsonElement raw)
        => String(raw, "failure_reason") ?? $"Skyvern run ended with status '{String(raw, "status")}'.";

    private static object ToolInput(Turn turn) => turn.Operation == "run" ? turn.Body
        : new { operation = turn.Operation, run_id = turn.RunId, artifact_id = turn.Controls["artifact_id"]?.GetValue<string>() };
    private static object MetadataValue(Turn turn) => new
    {
        agent_id = turn.Model[8..], operation = turn.Operation, run_id = turn.RunId,
        status = String(turn.Reply.Raw, "status"), wait_timed_out = turn.WaitTimedOut,
        raw = turn.Reply.Raw, headers = turn.Reply.Headers, uploads = turn.Uploads,
        artifacts = turn.Artifacts, warnings = turn.Warnings
    };
    private static Dictionary<string, object?> Metadata(Turn turn)
        => new() { ["skyvern"] = MetadataValue(turn) };
    private static Dictionary<string, Dictionary<string, object>> ProviderMetadata(Turn turn)
        => new() { ["skyvern"] = new() { ["raw"] = MetadataValue(turn), ["tool_name"] = turn.ToolName } };
    private static CallToolResult ToolResult(Turn turn) => new()
    {
        StructuredContent = JsonSerializer.SerializeToElement(MetadataValue(turn), Json),
        Content = [new TextContentBlock { Text = turn.Reply.Raw.GetRawText() }]
    };

    private static string ResultText(Turn turn)
    {
        if (Failed(turn.Reply.Raw)) return ErrorText(turn.Reply.Raw);
        var output = Property(turn.Reply.Raw, "output");
        if (output is { ValueKind: JsonValueKind.String } text) return text.GetString() ?? "";
        if (output is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } structured) return structured.GetRawText();
        if (turn.Operation is "upload" or "artifacts" or "artifact_content") return turn.Reply.Raw.GetRawText();
        return $"Skyvern run {turn.RunId}: {String(turn.Reply.Raw, "status") ?? "unknown"}.";
    }

    private AIResponse ToResponse(Turn turn)
    {
        var metadata = Metadata(turn);
        var status = String(turn.Reply.Raw, "status");
        var parts = new List<AIContentPart>
        {
            new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = turn.ToolId, ToolName = turn.ToolName,
                Title = "Skyvern " + turn.Operation, Input = ToolInput(turn), Output = ToolResult(turn),
                State = Failed(turn.Reply.Raw) ? "output-error" : "output-available", ProviderExecuted = true, Metadata = metadata
            },
            new AITextContentPart { Type = "text", Text = ResultText(turn), Metadata = metadata }
        };
        parts.AddRange(turn.Files);
        return new AIResponse
        {
            ProviderId = "skyvern", Model = turn.Model,
            Status = Failed(turn.Reply.Raw) ? "failed" : IsActive(turn.Reply.Raw) ? "in_progress" : status == "paused" ? "incomplete" : "completed",
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = parts, Metadata = metadata }], Metadata = metadata },
            Metadata = metadata
        };
    }

    private static AIStreamEvent Event(Turn turn, string type, object data, Dictionary<string, object?> metadata, string? id = null)
        => new()
        {
            ProviderId = "skyvern", Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id ?? turn.ToolId, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata }
        };
}
