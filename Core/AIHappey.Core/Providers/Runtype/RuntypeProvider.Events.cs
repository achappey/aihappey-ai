using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Runtype;

public sealed partial class RuntypeProvider
{
    private sealed record Frame(string? Cursor, string? EventName, JsonElement Data);

    private sealed class StreamState
    {
        public string? ExecutionId { get; set; }
        public bool IdentityEmitted { get; set; }
        public bool Terminal { get; set; }
        public bool SawText { get; set; }
        public HashSet<string> OpenTexts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OpenReasoning { get; } = new(StringComparer.Ordinal);
        public HashSet<string> InputEmitted { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ToolNames { get; } = new(StringComparer.Ordinal);
    }

    private async IAsyncEnumerable<AIStreamEvent> ReplayEvents(Route route, AIRequest request,
        System.Text.Json.Nodes.JsonObject options, [EnumeratorCancellation] CancellationToken ct)
    {
        var executionId = RequiredId(options, request, "executionId");
        var after = Text(options, "after");
        var conversationId = Text(options, "conversationId") ?? FindIdentity(request, "conversationId");
        var path = $"v1/agents/{Uri.EscapeDataString(route.AgentId!)}/executions/{Uri.EscapeDataString(executionId)}/events";
        var query = new List<string>();
        if (after is not null) query.Add("after=" + Uri.EscapeDataString(after));
        if (conversationId is not null) query.Add("conversationId=" + Uri.EscapeDataString(conversationId));
        if (query.Count > 0) path += "?" + string.Join("&", query);
        using var http = CreateRequest(HttpMethod.Get, path);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        var state = new StreamState { ExecutionId = executionId };
        await foreach (var frame in ReadFrames(response, ct))
            foreach (var evt in MapFrame(route, frame, state, Headers(response)))
                yield return evt;
    }

    private static async IAsyncEnumerable<Frame> ReadFrames(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? cursor = null, eventName = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 && data.ToString() != "[DONE]")
                    yield return new Frame(cursor, eventName, JsonDocument.Parse(data.ToString()).RootElement.Clone());
                data.Clear(); cursor = eventName = null;
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "id": cursor = value; break; // Opaque composite cursors must never be parsed as numbers.
                case "event": eventName = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }
        if (data.Length > 0 && data.ToString() != "[DONE]")
            yield return new Frame(cursor, eventName, JsonDocument.Parse(data.ToString()).RootElement.Clone());
    }

    private IEnumerable<AIStreamEvent> MapFrame(Route route, Frame frame, StreamState state,
        Dictionary<string, object?> headers)
    {
        var raw = frame.Data;
        var type = String(raw, "type") ?? frame.EventName ?? "unknown";
        var metadata = Metadata(raw, frame.Cursor, headers);
        metadata["runtype.eventType"] = type;
        var scoped = Scoped(raw, frame.Cursor);
        var executionId = String(raw, "executionId") ?? state.ExecutionId;
        state.ExecutionId = executionId;

        // Every upstream frame is available intact, including unrecognized, control, and progress events.
        yield return Event("data-runtype-event", frame.Cursor ?? executionId,
            new AIDataEventData { Id = frame.Cursor, Data = new { sseId = frame.Cursor, eventName = frame.EventName, raw = raw.Clone() } }, metadata);

        if (!state.IdentityEmitted && !string.IsNullOrWhiteSpace(executionId) && route.AgentId is not null)
        {
            state.IdentityEmitted = true;
            var identity = JsonSerializer.SerializeToElement(new
            {
                executionId, agentId = route.AgentId, conversationId = String(raw, "conversationId"), raw
            }, Json);
            var part = IdentityPart(route.AgentId, identity);
            yield return Event("tool-input-available", part.ToolCallId,
                new AIToolInputAvailableEventData { ToolName = IdentityTool, Input = part.Input!, ProviderExecuted = true,
                    ProviderMetadata = scoped }, metadata);
            yield return Event("tool-output-available", part.ToolCallId,
                new AIToolOutputAvailableEventData { ToolName = IdentityTool, Output = part.Output!, ProviderExecuted = true,
                    ProviderMetadata = scoped }, metadata);
        }

        var id = String(raw, "id") ?? executionId ?? "runtype-event";
        switch (type)
        {
            case "text_start":
                if (state.OpenTexts.Add(id))
                    yield return Event("text-start", id, new AITextStartEventData { ProviderMetadata = new() { ["runtype"] = raw.Clone() } }, metadata);
                break;
            case "text_delta":
                if (state.OpenTexts.Add(id)) yield return Event("text-start", id, new AITextStartEventData(), metadata);
                state.SawText = true;
                yield return Event("text-delta", id, new AITextDeltaEventData { Delta = String(raw, "delta") ?? "",
                    ProviderMetadata = new() { ["runtype"] = raw.Clone() } }, metadata);
                break;
            case "text_complete":
                if (state.OpenTexts.Add(id))
                {
                    yield return Event("text-start", id, new AITextStartEventData(), metadata);
                    if (String(raw, "text") is { Length: > 0 } completeText)
                    {
                        state.SawText = true;
                        yield return Event("text-delta", id, new AITextDeltaEventData { Delta = completeText }, metadata);
                    }
                }
                state.OpenTexts.Remove(id);
                yield return Event("text-end", id, new AITextEndEventData(), metadata);
                break;
            case "reasoning_start":
                if (state.OpenReasoning.Add(id))
                    yield return Event("reasoning-start", id, new AIReasoningStartEventData { ProviderMetadata = scoped }, metadata);
                break;
            case "reasoning_delta":
                if (state.OpenReasoning.Add(id)) yield return Event("reasoning-start", id, new AIReasoningStartEventData { ProviderMetadata = scoped }, metadata);
                yield return Event("reasoning-delta", id, new AIReasoningDeltaEventData
                { Delta = String(raw, "delta") ?? "", ProviderMetadata = scoped }, metadata);
                break;
            case "reasoning_complete":
                if (state.OpenReasoning.Add(id))
                {
                    yield return Event("reasoning-start", id, new AIReasoningStartEventData { ProviderMetadata = scoped }, metadata);
                    if (String(raw, "text") is { Length: > 0 } reasoning)
                        yield return Event("reasoning-delta", id, new AIReasoningDeltaEventData { Delta = reasoning, ProviderMetadata = scoped }, metadata);
                }
                state.OpenReasoning.Remove(id);
                yield return Event("reasoning-end", id, new AIReasoningEndEventData { ProviderMetadata = scoped }, metadata);
                break;
            case "tool_start":
            case "tool_input_complete":
                var callId = String(raw, "toolCallId") ?? id;
                var nativeName = String(raw, "toolName") ?? state.ToolNames.GetValueOrDefault(callId) ?? "runtype_tool";
                var name = SafeToolName(nativeName);
                state.ToolNames[callId] = name;
                if (state.InputEmitted.Add(callId))
                    yield return Event("tool-input-available", callId,
                        new AIToolInputAvailableEventData { ToolName = name, Title = nativeName,
                            Input = Property(raw, "parameters") ?? (object)new { }, ProviderExecuted = true,
                            ProviderMetadata = scoped }, metadata);
                break;
            case "tool_complete":
                var toolId = String(raw, "toolCallId") ?? id;
                var toolName = state.ToolNames.GetValueOrDefault(toolId) ?? SafeToolName(String(raw, "toolName") ?? "runtype_tool");
                if (!state.InputEmitted.Contains(toolId))
                {
                    state.InputEmitted.Add(toolId);
                    yield return Event("tool-input-available", toolId,
                        new AIToolInputAvailableEventData { ToolName = toolName, Input = new { }, ProviderExecuted = true,
                            ProviderMetadata = scoped }, metadata);
                }
                if (Property(raw, "success") is { ValueKind: JsonValueKind.False })
                    yield return Event("tool-output-error", toolId, new AIToolOutputErrorEventData
                    {
                        ToolCallId = toolId, ErrorText = String(raw, "error") ?? "Runtype tool failed.",
                        ProviderExecuted = true, ProviderMetadata = scoped
                    }, metadata);
                else
                    yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
                    {
                        ToolName = toolName, Output = new CallToolResult
                        { StructuredContent = Property(raw, "result") ?? raw.Clone() },
                        ProviderExecuted = true, ProviderMetadata = scoped
                    }, metadata);
                break;
            case "approval_start":
                if (String(raw, "approvalId") is { } approval && String(raw, "toolCallId") is { } approvalCall)
                    yield return Event("tool-approval-request", approvalCall,
                        new AIToolApprovalRequestEventData { ApprovalId = approval, ToolCallId = approvalCall }, metadata);
                break;
            case "source":
                if (String(raw, "url") is { Length: > 0 } sourceUrl)
                    yield return Event("source-url", id, new AISourceUrlEventData
                    { SourceId = id, Url = sourceUrl, Title = String(raw, "title"), Type = String(raw, "sourceType"),
                        ProviderMetadata = scoped }, metadata);
                break;
            case "media_complete":
                var mediaUrl = String(raw, "url");
                if (!string.IsNullOrEmpty(mediaUrl) || String(raw, "data") is { Length: > 0 })
                    yield return Event("file", id, new AIFileEventData
                    {
                        MediaType = String(raw, "mediaType") ?? "application/octet-stream",
                        Url = mediaUrl ?? $"data:{String(raw, "mediaType") ?? "application/octet-stream"};base64,{String(raw, "data")}",
                        ProviderMetadata = scoped
                    }, metadata);
                break;
            case "execution_error":
            case "error" when Property(raw, "recoverable") is not { ValueKind: JsonValueKind.True }:
                state.Terminal = true;
                yield return Event("error", executionId, new AIErrorEventData { ErrorText = ErrorText(raw) }, metadata);
                break;
            case "execution_complete":
                state.Terminal = true;
                foreach (var open in state.OpenTexts)
                    yield return Event("text-end", open, new AITextEndEventData(), metadata);
                state.OpenTexts.Clear();
                foreach (var open in state.OpenReasoning)
                    yield return Event("reasoning-end", open, new AIReasoningEndEventData(), metadata);
                state.OpenReasoning.Clear();
                if (!state.SawText && String(raw, "finalOutput") is { Length: > 0 } final)
                {
                    var finalId = $"runtype-final-{executionId}";
                    yield return Event("text-start", finalId, new AITextStartEventData(), metadata);
                    yield return Event("text-delta", finalId, new AITextDeltaEventData { Delta = final }, metadata);
                    yield return Event("text-end", finalId, new AITextEndEventData(), metadata);
                }
                if (Property(raw, "success") is { ValueKind: JsonValueKind.False })
                    yield return Event("error", executionId,
                        new AIErrorEventData { ErrorText = String(raw, "stopReason") ?? "Runtype execution failed." }, metadata);
                else
                    yield return Event("finish", executionId, Finish(route.Model, Usage(raw), metadata,
                        String(raw, "stopReason") ?? "stop"), metadata);
                break;
            // An await (including detached) is a continuation handle, not completion.
        }
    }

    private static string SafeToolName(string name)
        => name is "download_file" or "upload_files" or "generate_video" or "generate_speech"
            ? "runtype_" + name : name;

    private static string ErrorText(JsonElement raw)
    {
        var error = Property(raw, "error");
        return error is { ValueKind: JsonValueKind.String } text ? text.GetString()!
            : error is { } obj ? String(obj, "message") ?? obj.GetRawText()
            : String(raw, "code") ?? "Runtype execution failed.";
    }
}
