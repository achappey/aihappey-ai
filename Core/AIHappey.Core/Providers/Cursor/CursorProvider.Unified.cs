using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Cursor;

public sealed partial class CursorProvider
{
    private sealed class Execution(CursorApiClient api, AIRequest request, JsonElement options)
    {
        public CursorApiClient Api { get; } = api;
        public AIRequest Request { get; } = request;
        public JsonElement Options { get; } = options;
        public string? AgentId { get; set; }
        public string? RunId { get; set; }
        public JsonElement Created { get; set; } = Empty;
        public JsonElement Result { get; set; } = Empty;
        public JsonElement Usage { get; set; } = Empty;
        public JsonElement Artifacts { get; set; } = Empty;
        public List<AIContentPart> Content { get; } = [];
        public StringBuilder Text { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public Dictionary<string, JsonElement> Tools { get; } = new();
        public HashSet<string> StartedTools { get; } = [];
        public bool TextStarted { get; set; }
        public bool ReasoningStarted { get; set; }
        public string? RetentionSeconds { get; set; }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var execution = new Execution(Client(), request, NormalizeOptions(request));
        await foreach (var _ in Pipeline(execution, cancellationToken)) { }
        if (execution.Reasoning.Length > 0)
            execution.Content.Add(new AIReasoningContentPart { Type = "reasoning", Text = execution.Reasoning.ToString(), Metadata = Metadata(execution) });
        foreach (var (callId, raw) in execution.Tools)
            execution.Content.Add(ToolPart(callId, Str(raw, "name") ?? "cursor_tool", Prop(raw, "args") ?? Empty,
                Prop(raw, "result") ?? raw, Metadata(execution, raw), Str(raw, "status") == "completed" ? "output-available" : "input-available"));
        // The final result replaces accumulated assistant text, even if it is explicitly empty.
        var finalText = ResultText(execution.Result) ?? execution.Text.ToString();
        if (finalText.Length > 0) execution.Content.Add(new AITextContentPart { Type = "text", Text = finalText, Metadata = Metadata(execution) });
        var metadata = Metadata(execution);
        var status = Str(execution.Result, "status");
        return new AIResponse
        {
            ProviderId = "cursor", Model = request.Model ?? "cursor/default", Status = status is "ERROR" or "CANCELLED" or "EXPIRED" ? "failed" : "completed",
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = execution.Content, Metadata = metadata }], Metadata = metadata },
            Usage = Usage(execution.Usage), Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var execution = new Execution(Client(), request, NormalizeOptions(request));
        await foreach (var evt in Pipeline(execution, cancellationToken)) yield return evt;
    }

    private async IAsyncEnumerable<AIStreamEvent> Pipeline(Execution e, [EnumeratorCancellation] CancellationToken ct)
    {
        if (e.Request.Tools?.Count > 0) throw new NotSupportedException("Cursor does not execute local function tools. Configure remote MCP servers through cursor.body.mcpServers or cursor.mcpServers.");
        var route = ParseRoute(e.Request.Model);
        var body = Body(e.Options);
        var bodyEl = Element(body);
        var explicitAgent = Agree(Str(e.Options, "agentId"), Str(bodyEl, "agentId"), "agentId");
        e.AgentId = Agree(route.AgentId, explicitAgent, "agentId") ?? ReceiptAgent(e.Request);
        var suppliedModel = Prop(bodyEl, "model") is { } model ? Str(model, "id") : null;
        var modelId = Agree(route.ModelId, suppliedModel, "model.id");
        if (e.AgentId is not null && modelId is not null) throw new ArgumentException("Cursor existing-agent chat cannot select a launch model.");
        body["prompt"] = Prompt(e.Request);
        if (e.AgentId is null)
        {
            if (modelId is not null)
            {
                var modelBody = body["model"] as JsonObject ?? new JsonObject();
                modelBody["id"] = modelId; body["model"] = modelBody;
            }
            e.Created = await e.Api.CreateAgentAsync(body, ct);
            e.AgentId = Prop(e.Created, "agent") is { } agent ? Str(agent, "id") : null;
        }
        else
        {
            // Forward raw options to the fixed conversational endpoint. Upstream validates unknown fields.
            body.Remove("agentId"); body.Remove("model");
            e.Created = await e.Api.CreateRunAsync(e.AgentId, body, ct);
        }
        var run = Prop(e.Created, "run") ?? Empty;
        e.RunId = Str(run, "id");
        e.AgentId ??= Str(run, "agentId");
        if (string.IsNullOrEmpty(e.AgentId) || string.IsNullOrEmpty(e.RunId)) throw new InvalidOperationException("Cursor create response omitted agent/run identity.");
        var receipt = Element(new { agentId = e.AgentId, runId = e.RunId });
        var receiptId = "cursor-receipt-" + e.RunId;
        e.Content.Add(ToolPart(receiptId, ReceiptTool, receipt, receipt, Metadata(e)));
        foreach (var evt in ToolEvents(receiptId, ReceiptTool, receipt, receipt, Metadata(e))) yield return evt;

        await foreach (var frame in RunFrames(e, ct))
        {
            var metadata = Metadata(e, frame.Data);
            var scoped = Scoped(metadata);
            switch (frame.Event)
            {
                case "assistant":
                    var text = Str(frame.Data, "text") ?? "";
                    if (!e.TextStarted)
                    {
                        e.TextStarted = true;
                        yield return Evt("text-start", "cursor-text-" + e.RunId, new AITextStartEventData { ProviderMetadata = Loose(metadata) }, metadata);
                    }
                    e.Text.Append(text);
                    yield return Evt("text-delta", "cursor-text-" + e.RunId, new AITextDeltaEventData { Delta = text, ProviderMetadata = Loose(metadata) }, metadata);
                    break;
                case "thinking":
                    if (!e.ReasoningStarted)
                    {
                        e.ReasoningStarted = true;
                        yield return Evt("reasoning-start", "cursor-reasoning-" + e.RunId, new AIReasoningStartEventData { ProviderMetadata = scoped }, metadata);
                    }
                    var thinking = Str(frame.Data, "text") ?? "";
                    e.Reasoning.Append(thinking);
                    yield return Evt("reasoning-delta", "cursor-reasoning-" + e.RunId, new AIReasoningDeltaEventData { Delta = thinking, ProviderMetadata = scoped }, metadata);
                    break;
                case "tool_call":
                    var callId = Str(frame.Data, "callId") ?? throw new InvalidOperationException("Cursor tool_call omitted callId.");
                    var name = Str(frame.Data, "name") ?? "cursor_tool";
                    e.Tools[callId] = MergeTool(e.Tools.GetValueOrDefault(callId), frame.Data);
                    var tool = e.Tools[callId];
                    if (e.StartedTools.Add(callId))
                        yield return Evt("tool-input-available", callId, new AIToolInputAvailableEventData
                        { ToolName = name, Input = Prop(tool, "args") ?? Empty, ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
                    if (Str(frame.Data, "status") == "completed")
                        yield return Evt("tool-output-available", callId, new AIToolOutputAvailableEventData
                        { ToolName = name, Output = ToolResult(Prop(tool, "result") ?? tool), ProviderExecuted = true, Dynamic = true, ProviderMetadata = scoped }, metadata);
                    break;
                case "result": e.Result = frame.Data; yield return RawEvent(e, frame); break;
                case "error":
                    e.Content.Add(ToolPart("cursor-error-" + e.RunId, "cursor_error", Empty, frame.Data, metadata, "output-error"));
                    yield return Evt("error", e.RunId, new AIErrorEventData { ErrorText = "Cursor stream error: " + (Str(frame.Data, "code") ?? "unknown") }, metadata);
                    break;
                default:
                    // interaction_update is raw only. It must not duplicate simple mapped assistant/tool events.
                    yield return RawEvent(e, frame);
                    break;
            }
        }
        if (!e.TextStarted && ResultText(e.Result) is { Length: > 0 } finalText)
        {
            e.TextStarted = true;
            yield return Evt("text-start", "cursor-text-" + e.RunId, new AITextStartEventData { ProviderMetadata = Loose(Metadata(e)) }, Metadata(e));
            yield return Evt("text-delta", "cursor-text-" + e.RunId, new AITextDeltaEventData { Delta = finalText, ProviderMetadata = Loose(Metadata(e)) }, Metadata(e));
        }
        if (e.TextStarted) yield return Evt("text-end", "cursor-text-" + e.RunId, new AITextEndEventData { ProviderMetadata = Loose(Metadata(e)) }, Metadata(e));
        if (e.ReasoningStarted) yield return Evt("reasoning-end", "cursor-reasoning-" + e.RunId, new AIReasoningEndEventData { ProviderMetadata = Scoped(Metadata(e)) }, Metadata(e));
        e.Usage = await e.Api.GetUsageAsync(e.AgentId, new { runId = e.RunId }, ct);
        e.Artifacts = await e.Api.ListArtifactsAsync(e.AgentId, ct: ct);
        foreach (var artifact in Items(e.Artifacts))
        {
            if (Str(artifact, "path") is not { } path) continue;
            var download = await e.Api.DownloadArtifactAsync(e.AgentId, new { path }, ct);
            if (Str(download, "url") is not { } url) continue;
            var raw = Element(new { agentId = e.AgentId, path, artifact, download });
            var metadata = Metadata(e, raw);
            e.Content.Add(new AIFileContentPart { Type = "file", Filename = path, MediaType = "application/octet-stream", Data = url, Metadata = metadata });
            yield return Evt("file", path, new AIFileEventData { Filename = path, MediaType = "application/octet-stream", Url = url, ProviderMetadata = Scoped(metadata) }, metadata);
        }
        if (Str(e.Result, "status") is "ERROR" or "CANCELLED" or "EXPIRED")
            yield return Evt("error", e.RunId, new AIErrorEventData { ErrorText = "Cursor run ended with status " + Str(e.Result, "status") + "." }, Metadata(e));
        yield return Finish(e);
    }

    private static JsonElement MergeTool(JsonElement old, JsonElement current)
    {
        var obj = old.ValueKind == JsonValueKind.Object ? JsonNode.Parse(old.GetRawText())!.AsObject() : new JsonObject();
        foreach (var p in current.EnumerateObject()) obj[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        return Element(obj);
    }

    private async IAsyncEnumerable<CursorSseEvent> RunFrames(Execution e, [EnumeratorCancellation] CancellationToken ct)
    {
        var lastId = Str(e.Options, "lastEventId");
        var seen = new HashSet<(string Event, string Id)>();
        var attempts = Math.Clamp(Int(e.Options, "maxReconnects") ?? 4, 0, 10);
        var expired = false;
        for (var attempt = 0; attempt <= attempts; attempt++)
        {
            await using var enumerator = e.Api.StreamRunAsync(e.AgentId!, e.RunId!, lastId, ct: ct).GetAsyncEnumerator(ct);
            Exception? failure = null;
            var done = false;
            while (true)
            {
                CursorSseEvent? frame = null;
                try { if (await enumerator.MoveNextAsync()) frame = enumerator.Current; }
                catch (CursorApiException ex) when (ex.StatusCode == HttpStatusCode.Gone && ex.Code == "stream_expired") { expired = true; failure = ex; }
                catch (CursorApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests || (int?)ex.StatusCode >= 500) { failure = ex; }
                catch (HttpRequestException ex) when (ex is not CursorApiException) { failure = ex; }
                catch (IOException ex) { failure = ex; }
                if (frame is null) break;
                if (frame.Id is not null)
                {
                    lastId = frame.Id;
                    // IDs are opaque, and result/done can share an ID. Deduplicate by event AND ID.
                    if (frame.Id.Length > 0 && !seen.Add((frame.Event, frame.Id))) continue;
                }
                e.RetentionSeconds = frame.RetentionSeconds ?? e.RetentionSeconds;
                if (frame.Event == "result") e.Result = frame.Data;
                yield return frame;
                if (frame.Event == "done") { done = true; break; }
            }
            if (Terminal(Str(e.Result, "status"))) yield break;
            if (done || expired) break;
            if (attempt < attempts)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))), ct);
            _ = failure; // Disconnects never cancel or replay a mutation. After bounded reconnect, GET/poll.
        }
        var polls = Math.Clamp(Int(e.Options, "maxPolls") ?? 60, 1, 120);
        for (var poll = 0; poll < polls; poll++)
        {
            var run = await e.Api.GetRunAsync(e.AgentId!, e.RunId!, ct);
            yield return new CursorSseEvent("status", null, Element(new { runId = e.RunId, status = Str(run, "status") }), e.RetentionSeconds);
            if (Terminal(Str(run, "status")))
            {
                e.Result = run;
                yield return new CursorSseEvent("result", null, run, e.RetentionSeconds);
                yield return new CursorSseEvent("done", null, Empty, e.RetentionSeconds);
                yield break;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(poll, 5)))), ct);
        }
        throw new TimeoutException("Cursor run is still active after bounded stream recovery/polling; it has NOT been cancelled. Use the separate Cursor API wrapper to retrieve/resume the run using the receipt IDs.");
    }

    private static bool Terminal(string? status) => status is "FINISHED" or "ERROR" or "CANCELLED" or "EXPIRED";
    private static string? ResultText(JsonElement raw)
    {
        if (Str(raw, "text") is { } text) return text;
        return Prop(raw, "result") is { } result ? result.ValueKind == JsonValueKind.String ? result.GetString() : Str(result, "text") : null;
    }
    private static AIUsage Usage(JsonElement raw)
    {
        var total = Prop(raw, "totalUsage") ?? Empty;
        return new AIUsage { InputTokens = Int(total, "inputTokens"), OutputTokens = Int(total, "outputTokens"), TotalTokens = Int(total, "totalTokens"),
            CachedInputTokens = Int(total, "cacheReadTokens"), CacheWriteInputTokens = Int(total, "cacheWriteTokens") };
    }

    private static CallToolResult ToolResult(JsonElement raw) => new()
    {
        Content = [new TextContentBlock { Text = raw.GetRawText() }],
        StructuredContent = raw.ValueKind == JsonValueKind.Object ? raw.Clone() : Element(new { value = raw })
    };
    private static AIToolCallContentPart ToolPart(string id, string name, JsonElement input, JsonElement output, Dictionary<string, object?> metadata, string state = "output-available")
        => new() { Type = "tool-call", ToolCallId = id, ToolName = name, Input = input, Output = ToolResult(output), ProviderExecuted = true, State = state, Metadata = metadata };
    private static IEnumerable<AIStreamEvent> ToolEvents(string id, string name, JsonElement input, JsonElement output, Dictionary<string, object?> metadata)
    {
        yield return Evt("tool-input-available", id, new AIToolInputAvailableEventData { ToolName = name, Input = input, ProviderExecuted = true, ProviderMetadata = Scoped(metadata) }, metadata);
        yield return Evt("tool-output-available", id, new AIToolOutputAvailableEventData { ToolName = name, Output = ToolResult(output), ProviderExecuted = true, Dynamic = true, ProviderMetadata = Scoped(metadata) }, metadata);
    }
    private static AIStreamEvent Evt(string type, string? id, object data, Dictionary<string, object?> metadata)
        => new() { ProviderId = "cursor", Metadata = metadata, Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata } };
    private static AIStreamEvent RawEvent(Execution e, CursorSseEvent frame) => Evt("data-cursor-" + frame.Event, frame.Id,
        new AIDataEventData { Id = frame.Id, Data = Element(new { @event = frame.Event, id = frame.Id, data = Safe(frame.Data), retentionSeconds = frame.RetentionSeconds }), Transient = frame.Event == "heartbeat" }, Metadata(e, frame.Data));
    private static Dictionary<string, object?> Metadata(Execution e, JsonElement? raw = null) => new()
    {
        ["cursor"] = new { agentId = e.AgentId, runId = e.RunId, raw = Safe(raw ?? e.Result),
            created = Safe(e.Created), usage = Safe(e.Usage), artifacts = Safe(e.Artifacts), streamRetentionSeconds = e.RetentionSeconds }
    };
    private static Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> metadata)
        => new() { ["cursor"] = Element(metadata["cursor"]).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone()) };
    private static Dictionary<string, object> Loose(Dictionary<string, object?> metadata) => new() { ["cursor"] = metadata["cursor"]! };
    private static AIStreamEvent Finish(Execution e)
    {
        var usage = Usage(e.Usage);
        var metadata = Metadata(e);
        return Evt("finish", e.RunId, new AIFinishEventData
        {
            FinishReason = Str(e.Result, "status") is "ERROR" or "CANCELLED" or "EXPIRED" ? "error" : "stop",
            Model = e.Request.Model ?? "cursor/default", InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens,
            MessageMetadata = AIFinishMessageMetadata.Create(e.Request.Model ?? "cursor/default", DateTimeOffset.UtcNow,
                Prop(e.Usage, "totalUsage"), inputTokens: usage.InputTokens, outputTokens: usage.OutputTokens, totalTokens: usage.TotalTokens,
                cachedInputTokens: usage.CachedInputTokens, cachedInputWriteTokens: usage.CacheWriteInputTokens, additionalProperties: metadata)
        }, metadata);
    }

    private static JsonElement Safe(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return Empty;
        var node = JsonNode.Parse(value.GetRawText());
        void Redact(JsonNode? item)
        {
            if (item is JsonObject obj)
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    if (key.Equals("envVars", StringComparison.OrdinalIgnoreCase) || key.Equals("mcpServers", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("token", StringComparison.OrdinalIgnoreCase) && !key.EndsWith("Tokens", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("password", StringComparison.OrdinalIgnoreCase)) obj[key] = "[redacted]";
                    else Redact(obj[key]);
                }
            else if (item is JsonArray array) foreach (var child in array) Redact(child);
        }
        Redact(node);
        return Element(node);
    }
}
