using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.M8tes;

public partial class M8tesProvider
{
    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var c = await BeginAsync(request, true, cancellationToken);
        var state = new StreamState(c);
        // Identity is available before opening the long-lived connection, including for queued replies.
        foreach (var evt in StreamToolEvents(Receipt(c), state, RawMetadata(c.Raw), receipt: true))
            yield return evt;

        if (c.Queued || Str(c.Raw, "status") != "running")
        {
            foreach (var evt in StreamResponseEvents(await FinishAsync(request, c, cancellationToken), c, state))
                yield return evt;
            yield break;
        }

        Exception? transportError = null;
        var conflict = false;
        await using (var frames = StreamReadConnectionAsync(c, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                bool next;
                // Yielding outside the catch permits recovery from an interrupted HTTP body without retrying actions.
                try { next = await frames.MoveNextAsync(); }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                    && ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    transportError = ex;
                    break;
                }
                if (!next) break;
                var frame = frames.Current;
                if (frame.Conflict) { conflict = true; break; }
                foreach (var evt in StreamFrameEvents(frame, c, state)) yield return evt;
                if (state.Done) break;
            }
        }

        foreach (var evt in StreamCloseBlocks(state, RawMetadata(c.Raw))) yield return evt;
        if (conflict)
        {
            foreach (var evt in StreamResponseEvents(await FinishAsync(request, c, cancellationToken), c, state))
                yield return evt;
            yield break;
        }

        JsonObject? authoritative = null;
        Exception? statusError = null;
        try { authoritative = await GetRunAsync(c, cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { statusError = ex; }

        if (authoritative is not null) c.Raw = authoritative;
        // A native done frame is not proof of success (is_error=true can accompany subtype=success).
        // EOF, a broken body, and even done must be reconciled against persisted terminal/paused state.
        if (authoritative is not null && StreamSettled(Str(authoritative, "status")))
        {
            foreach (var evt in StreamResponseEvents(await FinishAsync(request, c, cancellationToken), c, state))
                yield return evt;
            yield break;
        }

        var failure = statusError?.Message ?? transportError?.Message
            ?? $"M8tes stream ended while run {c.RunId} was {Str(c.Raw, "status") ?? "in an unknown state"}.";
        var metadata = RawMetadata(c.Raw);
        foreach (var evt in StreamToolEvents(Receipt(c), state, metadata, receipt: true)) yield return evt;
        yield return StreamEvent("error", c.TurnId, new AIErrorEventData { ErrorText = failure }, metadata);
        yield return StreamEvent("finish", c.TurnId, new AIFinishEventData
        {
            Model = request.Model, FinishReason = "error",
            MessageMetadata = AIFinishMessageMetadata.Create(request.Model ?? "m8tes", DateTimeOffset.UtcNow,
                additionalProperties: metadata)
        }, metadata);
    }

    private static bool StreamSettled(string? status)
        => status is "completed" or "failed" or "cancelled" or "paused" or "awaiting_approval" or "closed" or "archived";

    private sealed record StreamFrame(string? EventName, string? Id, string Data, bool Conflict = false);

    private async IAsyncEnumerable<StreamFrame> StreamReadConnectionAsync(Conversation c,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.RunId)) throw new InvalidOperationException("M8tes did not return a run id.");
        // This verified route has no user_id parameter. BeginAsync has already validated run scope.
        using var message = HttpRequest(c, HttpMethod.Get, $"runs/{Uri.EscapeDataString(c.RunId)}/stream");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            yield return new StreamFrame(null, null, "", Conflict: true);
            yield break;
        }
        await EnsureSuccess(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var data = new StringBuilder();
        var hasData = false;
        string? eventName = null, id = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // ReadLine handles LF, CRLF and CR. Comments/keepalives are not dispatchable events.
            if (line.Length == 0)
            {
                if (hasData) yield return new StreamFrame(eventName, id, data.ToString());
                data.Clear(); hasData = false; eventName = id = null;
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..]; // SSE removes exactly one optional space.
            switch (field)
            {
                case "event": eventName = value; break;
                case "id" when !value.Contains('\0'): id = value; break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value); hasData = true;
                    break;
            }
        }
        // Some proxies omit the final blank line; retain that last frame, but never infer success from EOF.
        if (hasData) yield return new StreamFrame(eventName, id, data.ToString());
    }

    private sealed class StreamBlock(string id, string kind, string? messageId)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string? MessageId { get; } = messageId;
        public string? ToolName { get; set; }
        public string? Signature { get; set; }
        public StringBuilder Text { get; } = new();
        public bool Started { get; set; }
        public bool Closed { get; set; }
        public bool InputAvailable { get; set; }
    }

    private sealed class StreamState(Conversation c)
    {
        public string Prefix { get; } = $"m8tes-{c.TurnId}";
        public Dictionary<string, StreamBlock> Blocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> Messages { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SeenFrames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> InputStarts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Inputs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Outputs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Approvals { get; } = new(StringComparer.Ordinal);
        public StringBuilder Text { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public string? CurrentMessage { get; set; }
        public bool? CurrentMessageAccepted { get; set; }
        public bool Done { get; set; }
        public bool Error { get; set; }
    }

    private IEnumerable<AIStreamEvent> StreamFrameEvents(StreamFrame frame, Conversation c, StreamState state)
    {
        JsonObject? raw = null;
        try { raw = JsonNode.Parse(frame.Data) as JsonObject; }
        catch (JsonException) { }
        if (raw is null)
        {
            yield return StreamRawFrame(frame, frame.Data, c, "unrecognized_payload");
            if (frame.Data.Trim() == "[DONE]") state.Done = true;
            yield break;
        }

        var type = Str(raw, "type") ?? frame.EventName ?? "unknown";
        var message = raw["message"] as JsonObject;
        var messageId = Str(raw, "message_id") ?? Str(message, "id");
        var blockId = Str(raw, "id") ?? Str(raw, "tool_use_id") ?? Str(raw["content_block"], "id");
        var index = StreamString(raw, "index");
        var blockKey = blockId ?? $"{messageId ?? state.CurrentMessage ?? "anonymous"}:{index ?? "0"}";
        var sequence = StreamSequence(raw) ?? StreamSequence(message) ?? StreamSequence(raw["event_metadata"]);
        var identity = frame.Id ?? Str(raw, "event_id");
        // Block id alone identifies a block, NOT a delta: repeated words are legitimate token chunks.
        var deltaSequence = StreamString(raw, "delta_sequence");
        var dedup = identity is not null ? $"event:{identity}:{type}"
            : type == "content_block_delta" ? deltaSequence is not null
                ? $"delta:{messageId}:{blockKey}:{deltaSequence}" : null
            : sequence is not null ? $"sequence:{sequence}:{type}:{blockKey}:{raw.ToJsonString()}" : null;
        if (dedup is not null && !state.SeenFrames.Add(dedup)) yield break;

        bool? accepted = sequence is not null && c.Followup ? sequence > c.Baseline : null;
        if (messageId is not null && accepted is null && state.Messages.TryGetValue(messageId, out var known))
            accepted = known;
        if (type == "message_start")
        {
            state.CurrentMessage = messageId;
            state.CurrentMessageAccepted = accepted ?? (!c.Followup ? true : null);
        }
        if (accepted is not null && messageId is not null) state.Messages[messageId] = accepted.Value;
        var indexKey = $"{messageId ?? state.CurrentMessage ?? "anonymous"}:{index ?? "0"}";
        if (blockId is null && state.Blocks.ContainsKey(indexKey)) blockKey = indexKey;
        if (accepted is null && state.Blocks.ContainsKey(blockKey)) accepted = true;
        if (accepted is null && messageId is null) accepted = state.CurrentMessageAccepted;
        accepted ??= !c.Followup;

        var control = type is "metadata" or "run_metrics" or "done" or "error" or "run-error";
        if (!accepted.Value && !control)
        {
            yield return StreamRawFrame(frame, raw, c, sequence is not null ? "prior_turn" : "uncorrelated_replay");
            yield break;
        }
        if (sequence is not null && sequence <= c.Baseline && c.Followup)
        {
            yield return StreamRawFrame(frame, raw, c, "prior_turn");
            yield break;
        }

        var metadata = RawMetadata(raw);
        switch (type)
        {
            case "metadata":
            case "run_metrics":
                yield return StreamRawFrame(frame, raw, c);
                break;
            case "done":
                yield return StreamRawFrame(frame, raw, c);
                // Old/uncorrelated done frames in a full replay must not terminate a follow-up's live tail.
                if (accepted.Value) state.Done = true;
                break;
            case "error":
            case "run-error":
                yield return StreamRawFrame(frame, raw, c);
                // Uncorrelated follow-up errors can also be from history; the final resource decides failure.
                if (accepted.Value)
                {
                    state.Error = true;
                    yield return StreamEvent("error", c.TurnId, new AIErrorEventData
                    { ErrorText = Str(raw, "message") ?? Str(raw, "error") ?? raw.ToJsonString() }, metadata);
                }
                break;
            case "content_block_start":
            {
                var content = raw["content_block"] as JsonObject ?? raw;
                var kind = Str(raw, "block_type") ?? Str(content, "type") ?? "text";
                var block = StreamGetBlock(state, blockKey, kind, messageId);
                // Claude may identify starts by id but subsequent deltas/stops only by index.
                if (index is not null) state.Blocks[indexKey] = block;
                if (kind == "tool_use")
                {
                    block.ToolName = Str(content, "name") ?? Str(raw, "name");
                    if (state.InputStarts.Add(block.Id))
                        yield return StreamEvent("tool-input-start", block.Id, new AIToolInputStartEventData
                        { ToolName = block.ToolName ?? "m8tes_tool", ProviderExecuted = true,
                            ProviderMetadata = StreamScope(metadata) }, metadata);
                }
                else if (kind is "text" or "thinking" or "reasoning")
                {
                    foreach (var evt in StreamAppend(block, Str(content, kind == "text" ? "text" : "thinking")
                        ?? Str(content, "reasoning") ?? "", state, metadata)) yield return evt;
                }
                else yield return StreamRawFrame(frame, raw, c);
                break;
            }
            case "content_block_delta":
            {
                var delta = raw["delta"] as JsonObject;
                var kind = Str(delta, "type");
                var block = StreamGetBlock(state, blockKey,
                    kind is "thinking_delta" or "reasoning_delta" or "signature_delta" ? "thinking"
                        : kind == "input_json_delta" ? "tool_use" : "text", messageId);
                if (kind == "input_json_delta")
                {
                    var chunk = Str(delta, "partial_json") ?? "";
                    block.Text.Append(chunk);
                    if (state.InputStarts.Add(block.Id))
                        yield return StreamEvent("tool-input-start", block.Id, new AIToolInputStartEventData
                        { ToolName = block.ToolName ?? "m8tes_tool", ProviderExecuted = true,
                            ProviderMetadata = StreamScope(metadata) }, metadata);
                    yield return StreamEvent("tool-input-delta", block.Id,
                        new AIToolInputDeltaEventData { InputTextDelta = chunk }, metadata);
                }
                else if (kind == "signature_delta") block.Signature = Str(delta, "signature");
                else if (kind is "text_delta" or "thinking_delta" or "reasoning_delta")
                {
                    foreach (var evt in StreamAppend(block, Str(delta, "text") ?? Str(delta, "thinking")
                        ?? Str(delta, "reasoning") ?? "", state, metadata)) yield return evt;
                }
                else yield return StreamRawFrame(frame, raw, c);
                break;
            }
            case "content_block_stop":
                if (state.Blocks.TryGetValue(blockKey, out var stopped))
                    foreach (var evt in StreamCloseBlock(stopped, state, metadata)) yield return evt;
                else yield return StreamRawFrame(frame, raw, c);
                break;
            case "tool_use":
            {
                var id = blockId ?? $"{state.Prefix}-tool-{state.Blocks.Count}";
                var block = StreamGetBlock(state, id, "tool_use", messageId);
                block.ToolName = Str(raw, "name") ?? block.ToolName;
                foreach (var evt in StreamToolEvents(new AIToolCallContentPart
                {
                    Type = "tool-call", ToolCallId = block.Id, ToolName = block.ToolName,
                    Input = Element(raw["input"] ?? StreamParse(block.Text.ToString())), ProviderExecuted = true
                }, state, metadata)) yield return evt;
                block.InputAvailable = true;
                break;
            }
            case "tool_result":
            {
                var id = Str(raw, "tool_use_id") ?? blockId;
                if (id is null) { yield return StreamRawFrame(frame, raw, c, "missing_tool_identity"); break; }
                var toolId = state.Blocks.TryGetValue(id, out var tool) ? tool.Id : id;
                if (!state.Outputs.Add(toolId)) break;
                if (StreamBoolean(raw, "is_error"))
                    yield return StreamEvent("tool-output-error", toolId, new AIToolOutputErrorEventData
                    { ToolCallId = toolId, ErrorText = Str(raw, "content") ?? Str(raw, "result") ?? raw.ToJsonString(),
                        ProviderExecuted = true, ProviderMetadata = StreamScope(metadata) }, metadata);
                else
                    yield return StreamEvent("tool-output-available", toolId, new AIToolOutputAvailableEventData
                    { ToolName = tool?.ToolName, Output = Element(raw["result"] ?? raw["content"]),
                        ProviderExecuted = true, ProviderMetadata = StreamScope(metadata) }, metadata);
                break;
            }
            case "assistant":
            case "message":
            {
                // Full-message replay must not repeat text already received as native deltas.
                var role = Str(message, "role") ?? Str(raw, "role");
                if (role is not null && role != "assistant")
                { yield return StreamRawFrame(frame, raw, c, "non_assistant_message"); break; }
                var content = message?["content"] ?? raw["content"] ?? raw["content_blocks"];
                if (content is JsonArray parts)
                {
                    for (var i = 0; i < parts.Count; i++)
                    {
                        if (parts[i] is not JsonObject part) continue;
                        var kind = Str(part, "type");
                        var key = Str(part, "id") ?? $"{messageId ?? state.CurrentMessage ?? "anonymous"}:{i}";
                        if (kind is "text" or "thinking" or "reasoning")
                        {
                            if (!state.Blocks.ContainsKey(key) && state.Blocks.Values.Any(b => b.Kind == kind && b.Text.Length > 0))
                            {
                                yield return StreamRawFrame(frame, part, c, "uncorrelated_full_replay");
                                continue;
                            }
                            var block = StreamGetBlock(state, key, kind, messageId);
                            var full = Str(part, kind == "text" ? "text" : "thinking") ?? Str(part, "reasoning") ?? "";
                            var existing = block.Text.ToString();
                            if (full.StartsWith(existing, StringComparison.Ordinal))
                                foreach (var evt in StreamAppend(block, full[existing.Length..], state, metadata)) yield return evt;
                            else yield return StreamRawFrame(frame, part, c, "non_append_replay");
                        }
                        else if (kind is "tool_use" or "tool_result")
                        {
                            var nested = (JsonObject)part.DeepClone();
                            if (sequence is not null) nested["sequence"] = sequence.Value;
                            if (messageId is not null) nested["message_id"] = messageId;
                            foreach (var evt in StreamFrameEvents(new StreamFrame(null, null, nested.ToJsonString()), c, state))
                                yield return evt;
                        }
                        else yield return StreamRawFrame(frame, part, c);
                    }
                }
                else if (content is JsonValue value && value.TryGetValue<string>(out var full))
                {
                    if (!state.Blocks.ContainsKey(blockKey) && state.Text.Length > 0)
                    { yield return StreamRawFrame(frame, raw, c, "uncorrelated_full_replay"); break; }
                    var block = StreamGetBlock(state, blockKey, "text", messageId);
                    var existing = block.Text.ToString();
                    if (full.StartsWith(existing, StringComparison.Ordinal))
                        foreach (var evt in StreamAppend(block, full[existing.Length..], state, metadata)) yield return evt;
                    else yield return StreamRawFrame(frame, raw, c, "non_append_replay");
                }
                else yield return StreamRawFrame(frame, raw, c);
                break;
            }
            default:
                yield return StreamRawFrame(frame, raw, c);
                break;
        }
        if (type == "message_stop") { state.CurrentMessage = null; state.CurrentMessageAccepted = null; }
    }

    private static StreamBlock StreamGetBlock(StreamState state, string key, string kind, string? messageId)
    {
        if (!state.Blocks.TryGetValue(key, out var block))
        {
            block = new StreamBlock(key, kind, messageId);
            state.Blocks.Add(key, block);
        }
        return block;
    }

    private IEnumerable<AIStreamEvent> StreamAppend(StreamBlock block, string text, StreamState state,
        Dictionary<string, object?> metadata)
    {
        if (text.Length == 0) yield break;
        // A full replay may add a suffix after content_block_stop; use a fresh lifecycle for that suffix.
        if (block.Closed) { block.Closed = false; block.Started = false; }
        var reasoning = block.Kind is "thinking" or "reasoning";
        if (!block.Started)
        {
            block.Started = true;
            yield return reasoning
                ? StreamEvent("reasoning-start", block.Id, new AIReasoningStartEventData
                    { ProviderMetadata = StreamScope(metadata) }, metadata)
                : StreamEvent("text-start", block.Id, new AITextStartEventData(), metadata);
        }
        block.Text.Append(text);
        (reasoning ? state.Reasoning : state.Text).Append(text);
        yield return reasoning
            ? StreamEvent("reasoning-delta", block.Id, new AIReasoningDeltaEventData
                { Delta = text, Signature = block.Signature, ProviderMetadata = StreamScope(metadata) }, metadata)
            : StreamEvent("text-delta", block.Id, new AITextDeltaEventData { Delta = text }, metadata);
    }

    private IEnumerable<AIStreamEvent> StreamCloseBlock(StreamBlock block, StreamState state,
        Dictionary<string, object?> metadata)
    {
        if (block.Closed) yield break;
        block.Closed = true;
        if (block.Kind == "tool_use")
        {
            if (!block.InputAvailable && block.Text.Length > 0)
            {
                var input = StreamParse(block.Text.ToString());
                if (input is JsonObject or JsonArray)
                {
                    foreach (var evt in StreamToolEvents(new AIToolCallContentPart
                    { Type = "tool-call", ToolCallId = block.Id, ToolName = block.ToolName,
                        Input = Element(input), ProviderExecuted = true }, state, metadata)) yield return evt;
                    block.InputAvailable = true;
                }
                else yield return StreamEvent("data-m8tes", block.Id, new AIDataEventData
                { Data = new { reason = "incomplete_tool_input", input = block.Text.ToString() } }, metadata);
            }
        }
        else if (block.Started)
            yield return block.Kind is "thinking" or "reasoning"
                ? StreamEvent("reasoning-end", block.Id, new AIReasoningEndEventData
                    { Signature = block.Signature, ProviderMetadata = StreamScope(metadata) }, metadata)
                : StreamEvent("text-end", block.Id, new AITextEndEventData(), metadata);
    }

    private IEnumerable<AIStreamEvent> StreamCloseBlocks(StreamState state, Dictionary<string, object?> metadata)
    {
        foreach (var block in state.Blocks.Values.Distinct())
            foreach (var evt in StreamCloseBlock(block, state, metadata)) yield return evt;
    }

    private IEnumerable<AIStreamEvent> StreamToolEvents(AIToolCallContentPart tool, StreamState state,
        Dictionary<string, object?>? metadata, bool receipt = false)
    {
        var scoped = StreamScope(tool.Metadata ?? metadata);
        // Receipt output is intentionally repeated with the SAME identity to update pending/final state.
        if (state.Inputs.Add(tool.ToolCallId))
            yield return StreamEvent("tool-input-available", tool.ToolCallId, new AIToolInputAvailableEventData
            { ToolName = tool.ToolName ?? "m8tes_tool", Title = tool.Title, Input = tool.Input ?? Element(new { }),
                ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
        if (tool.Approval is { Approved: not true } approval && state.Approvals.Add(approval.Id ?? tool.ToolCallId))
            yield return StreamEvent("tool-approval-request", tool.ToolCallId, new AIToolApprovalRequestEventData
            { ApprovalId = approval.Id ?? tool.ToolCallId, ToolCallId = tool.ToolCallId }, metadata);
        if (tool.Output is null) yield break;
        if (!receipt && !state.Outputs.Add(tool.ToolCallId)) yield break;
        if (tool.State == "output-error")
            yield return StreamEvent("tool-output-error", tool.ToolCallId, new AIToolOutputErrorEventData
            { ToolCallId = tool.ToolCallId, ErrorText = tool.Output.ToString() ?? "M8tes tool failed.",
                ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
        else
            yield return StreamEvent("tool-output-available", tool.ToolCallId, new AIToolOutputAvailableEventData
            { ToolName = tool.ToolName, Output = tool.Output, ProviderExecuted = true,
                ProviderMetadata = scoped }, metadata);
    }

    private IEnumerable<AIStreamEvent> StreamResponseEvents(AIResponse result, Conversation c, StreamState state)
    {
        var metadata = result.Metadata ?? RawMetadata(c.Raw);
        foreach (var evt in StreamToolEvents(Receipt(c), state, metadata, receipt: true)) yield return evt;
        var parts = result.Output?.Items?.SelectMany(item => item.Content ?? []).ToList() ?? [];
        var finalText = string.Concat(parts.OfType<AITextContentPart>().Select(part => part.Text));
        var streamed = state.Text.ToString();
        if (!c.Queued && finalText.Length > 0)
        {
            if (finalText.StartsWith(streamed, StringComparison.Ordinal))
            {
                var block = StreamGetBlock(state, $"{state.Prefix}-final", "text", null);
                foreach (var evt in StreamAppend(block, finalText[streamed.Length..], state, metadata)) yield return evt;
            }
            else
                yield return StreamEvent("data-m8tes", c.TurnId, new AIDataEventData
                { Data = new { reason = "final_text_differs_from_stream", text = finalText } }, metadata);
        }
        var finalReasoning = string.Concat(parts.OfType<AIReasoningContentPart>().Select(part => part.Text));
        var streamedReasoning = state.Reasoning.ToString();
        if (!c.Queued && finalReasoning.StartsWith(streamedReasoning, StringComparison.Ordinal))
        {
            var block = StreamGetBlock(state, $"{state.Prefix}-final-reasoning", "thinking", null);
            foreach (var evt in StreamAppend(block, finalReasoning[streamedReasoning.Length..], state, metadata)) yield return evt;
        }
        foreach (var part in parts)
        {
            if (part is AIToolCallContentPart tool && !c.Queued)
            {
                if (tool.ToolCallId == Receipt(c).ToolCallId) continue;
                foreach (var evt in StreamToolEvents(tool, state, metadata)) yield return evt;
            }
            else if (part is AIFileContentPart file && !c.Queued)
            {
                var url = StreamFileUrl(file);
                if (url is not null)
                    yield return StreamEvent("file", $"{state.Prefix}-file-{file.Filename}", new AIFileEventData
                    { MediaType = file.MediaType ?? "application/octet-stream", Filename = file.Filename,
                        Url = url, ProviderMetadata = StreamScope(file.Metadata ?? metadata) }, metadata);
                else yield return StreamEvent("data-m8tes", c.TurnId, new AIDataEventData
                { Data = new { reason = "unrecognized_file_data", file } }, metadata);
            }
        }
        foreach (var evt in StreamCloseBlocks(state, metadata)) yield return evt;
        var status = result.Status ?? Str(c.Raw, "status");
        var failed = !c.Queued && status is "failed" or "cancelled" or "error";
        var paused = status is "paused" or "awaiting_approval";
        var reason = c.Queued ? "other" : failed || state.Error ? "error" : paused ? "tool-calls"
            : status is "completed" or "closed" ? "stop" : "other";
        if (failed && !state.Error)
            yield return StreamEvent("error", c.TurnId, new AIErrorEventData
            { ErrorText = Str(c.Raw, "error") ?? $"M8tes run {status}." }, metadata);
        yield return StreamEvent("finish", c.TurnId, new AIFinishEventData
        {
            Model = result.Model, FinishReason = reason,
            MessageMetadata = AIFinishMessageMetadata.Create(result.Model ?? "m8tes", DateTimeOffset.UtcNow,
                result.Usage, additionalProperties: metadata)
        }, metadata);
    }

    private AIStreamEvent StreamRawFrame(StreamFrame frame, object raw, Conversation c, string? reason = null)
        => StreamEvent("data-m8tes", frame.Id ?? c.TurnId, new AIDataEventData
        { Id = frame.Id, Data = new { event_name = frame.EventName, sse_id = frame.Id, reason, raw = Element(raw) } },
            RawMetadata(raw));

    private AIStreamEvent StreamEvent(string type, string? id, object data, Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope
            { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata }
        };

    private static Dictionary<string, Dictionary<string, object>>? StreamScope(Dictionary<string, object?>? metadata)
        => metadata is null ? null : new()
        { ["m8tes"] = metadata.ToDictionary(pair => pair.Key, pair => pair.Value!) };

    private static long? StreamSequence(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        foreach (var key in new[] { "sequence", "sequence_number", "message_sequence" })
            if (long.TryParse(StreamString(obj, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
        return null;
    }

    private static string? StreamString(JsonNode? node, string key)
        => node is JsonObject obj && obj[key] is JsonValue value ? value.ToString() : null;

    private static bool StreamBoolean(JsonNode? node, string key)
        => node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static JsonNode? StreamParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    private static string? StreamFileUrl(AIFileContentPart file)
    {
        if (file.Data is string url) return url;
        if (file.Data is JsonElement { ValueKind: JsonValueKind.String } json) return json.GetString();
        if (file.Data is byte[] bytes)
            return $"data:{file.MediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes)}";
        return null;
    }
}
