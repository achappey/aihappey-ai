using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.CamelAI;

public sealed partial class CamelAIProvider
{
    private sealed class DisplayState
    {
        public HashSet<string> Texts { get; } = [];
        public HashSet<string> Reasoning { get; } = [];
        public Dictionary<string, string> ToolNames { get; } = [];
        public Dictionary<string, StringBuilder> Text { get; } = [];
        public HashSet<string> Files { get; } = [];
        public string? MessageIndex { get; set; }
    }
    private sealed record Frame(string? Cursor, JsonElement Data);

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = AgentId(request.Model);
        var options = Options(request);
        using var deadline = Deadline(options, cancellationToken);
        using var displayCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var ct = deadline.Token;
        var operation = Text(options, "operation") ?? "prompt";
        // Establish a cursor before dispatch, so a fast turn is replayable without prior-turn leakage.
        string? cursor = null;
        if (operation == "prompt")
        {
            var state = await SendJson(HttpMethod.Get, $"{AgentPath(agent)}/state", null, ct);
            cursor = Property(state, "cursor")?.ToString();
        }
        var record = await Begin(agent, request, options, ct);
        var id = String(record, "id") ?? throw new JsonException("CamelAI request returned no id.");
        var metadata = Metadata(agent, record);
        yield return Event("data-camelai-request", id, new AIDataEventData { Data = record.Clone() }, metadata);
        var stateDisplay = new DisplayState();
        var channel = Channel.CreateBounded<Frame>(128);
        var producer = String(record, "state") == "running"
            ? Watch(agent, cursor, channel.Writer, options, displayCancellation.Token) : Task.CompletedTask;
        if (producer == Task.CompletedTask) channel.Writer.TryComplete();
        var settled = Settle(agent, record, ct);
        try
        {
            // Outcome polling is independent of display events: a dropped response frame cannot hang a run.
            while (!settled.IsCompleted)
            {
                var available = channel.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(settled, available) == settled) break;
                if (!await available) break;
                while (channel.Reader.TryRead(out var frame))
                    foreach (var evt in MapFrame(agent, id, frame, stateDisplay)) yield return evt;
            }
            record = await settled;
            // Drain frames already delivered, without waiting for the agent-wide watcher to end.
            while (channel.Reader.TryRead(out var frame))
                foreach (var evt in MapFrame(agent, id, frame, stateDisplay)) yield return evt;
            var response = await MapResponse(agent, record, ct);
            foreach (var open in stateDisplay.Texts)
                yield return Event("text-end", open, new AITextEndEventData(), response.Metadata);
            foreach (var open in stateDisplay.Reasoning)
                yield return Event("reasoning-end", open, new AIReasoningEndEventData(), response.Metadata);

            var finalText = response.Output?.Items?.SelectMany(item => item.Content ?? []).OfType<AITextContentPart>().FirstOrDefault()?.Text;
            var result = Property(Property(record, "outcome") ?? default, "result") ?? default;
            var finalIndex = Property(result, "replyIndex")?.ToString();
            var displayed = finalIndex is not null && stateDisplay.Text.TryGetValue(finalIndex, out var builder) ? builder.ToString() : "";
            if (finalText is not null && finalText != displayed)
            {
                if (displayed.Length == 0 || finalText.StartsWith(displayed, StringComparison.Ordinal))
                {
                    var textId = $"camelai-final-{id}";
                    yield return Event("text-start", textId, new AITextStartEventData(), response.Metadata);
                    yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = finalText[displayed.Length..] }, response.Metadata);
                    yield return Event("text-end", textId, new AITextEndEventData(), response.Metadata);
                }
                // Retractions/snapshots cannot undo bytes already sent through OpenAI-style streams.
                // Preserve the authoritative answer as explicit reconciliation metadata, never append a duplicate.
                yield return Event("data-camelai-result", id, new AIDataEventData { Data = new { text = finalText, raw = record } }, response.Metadata);
            }
            foreach (var file in response.Output?.Items?.SelectMany(item => item.Content ?? []).OfType<AIFileContentPart>() ?? [])
                if (file.Data?.ToString() is { } url && stateDisplay.Files.Add(url))
                    yield return Event("file", id, new AIFileEventData { Url = url, MediaType = file.MediaType ?? "application/octet-stream", Filename = file.Filename }, response.Metadata);
            if (response.Status == "failed")
                yield return Event("error", id, new AIErrorEventData { ErrorText = String(record, "error") ?? String(result, "error") ?? "CamelAI run stopped at a limit." }, response.Metadata);
            var usage = response.Usage as AIUsage;
            yield return Event("finish", id, new AIFinishEventData
            {
                FinishReason = response.Status == "failed" ? "error" : response.Status == "in_progress" ? "other" : "stop",
                Model = response.Model, InputTokens = usage?.InputTokens, OutputTokens = usage?.OutputTokens, TotalTokens = usage?.TotalTokens,
                MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, DateTimeOffset.UtcNow, usage,
                    inputTokens: usage?.InputTokens, outputTokens: usage?.OutputTokens, totalTokens: usage?.TotalTokens,
                    additionalProperties: response.Metadata)
            }, response.Metadata);
        }
        finally
        {
            displayCancellation.Cancel();
            try { await producer; } catch (OperationCanceledException) when (displayCancellation.IsCancellationRequested) { }
            // Observe an outstanding outcome poll when a consumer disposes the iterator early.
            deadline.Cancel();
            try { await settled; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    private async Task Watch(string agent, string? cursor, ChannelWriter<Frame> writer, JsonObject options, CancellationToken ct)
    {
        try
        {
            for (var reconnect = 0; reconnect < 4; reconnect++)
            {
                try
                {
                    using var http = CreateRequest(HttpMethod.Get, $"{AgentPath(agent)}/events?snapshot=1"
                        + (options["subagents"]?.GetValue<bool>() == true ? "&subagents=1" : ""));
                    http.Headers.Accept.ParseAdd("text/event-stream");
                    if (cursor is not null) http.Headers.Add("Last-Event-ID", cursor);
                    using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        cursor = null; // Watcher snapshot is the documented replay-gap recovery.
                        continue;
                    }
                    await EnsureSuccess(response, ct);
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                    var data = new StringBuilder(); string? frameCursor = null;
                    while (!ct.IsCancellationRequested)
                    {
                        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
                        var line = await reader.ReadLineAsync(heartbeat.Token);
                        if (line is null) break;
                        if (line.Length == 0 && data.Length > 0)
                        {
                            using var document = JsonDocument.Parse(data.ToString());
                            var raw = document.RootElement.Clone();
                            if (frameCursor is null || !long.TryParse(frameCursor, out var incoming) || !long.TryParse(cursor, out var previous) || incoming > previous)
                                await writer.WriteAsync(new Frame(frameCursor, raw), ct);
                            cursor = frameCursor ?? Property(raw, "cursor")?.ToString() ?? cursor;
                            data.Clear(); frameCursor = null;
                        }
                        else if (line.StartsWith("id:", StringComparison.Ordinal)) frameCursor = line[3..].Trim();
                        else if (line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            if (data.Length > 0) data.Append('\n');
                            data.Append(line[5..].TrimStart());
                            if (data.Length > 4 * 1024 * 1024) throw new IOException("CamelAI SSE frame exceeds 4 MiB.");
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
                {
                    // Display transport is optional; request polling still determines success/failure.
                    await writer.WriteAsync(new Frame(null, Serialize(new { type = "transport_warning", message = ex.Message })), ct);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (reconnect + 1)), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { writer.TryComplete(); }
    }

    private static IEnumerable<AIStreamEvent> MapFrame(string agent, string requestId, Frame frame, DisplayState state)
    {
        var raw = frame.Data;
        var type = String(raw, "type");
        // Do not leak another run's events on a shared agent. Steering is settled through our own outcome.
        var belongs = type == "response" ? String(raw, "id") == requestId : String(raw, "requestId") == requestId;
        if (!belongs && type != "transport_warning") yield break;
        var metadata = Metadata(agent, raw);
        ((Dictionary<string, object?>)metadata["camelai"]!)["cursor"] = frame.Cursor;
        yield return Event("data-camelai-event", frame.Cursor, new AIDataEventData { Data = new { cursor = frame.Cursor, raw } }, metadata);
        if (Property(raw, "event") is not { ValueKind: JsonValueKind.Object } item) yield break;
        var eventType = String(item, "type");
        var index = Property(item, "index")?.ToString();
        if (eventType == "message_start") state.MessageIndex = index;
        if (eventType == "message_update" && Property(item, "assistantMessageEvent") is { } update)
        {
            var kind = String(update, "type");
            var messageIndex = index ?? state.MessageIndex ?? "unknown";
            var part = Property(update, "contentIndex")?.ToString() ?? "0";
            var id = $"camelai-{requestId}-{messageIndex}-{part}";
            if (kind == "text_delta")
            {
                if (state.Texts.Add(id)) yield return Event("text-start", id, new AITextStartEventData(), metadata);
                var delta = String(update, "delta") ?? "";
                if (!state.Text.TryGetValue(messageIndex, out var builder)) state.Text[messageIndex] = builder = new();
                builder.Append(delta);
                yield return Event("text-delta", id, new AITextDeltaEventData { Delta = delta, ProviderMetadata = new() { ["camelai"] = raw } }, metadata);
            }
            else if (kind == "thinking_delta")
            {
                if (state.Reasoning.Add(id)) yield return Event("reasoning-start", id, new AIReasoningStartEventData(), metadata);
                yield return Event("reasoning-delta", id, new AIReasoningDeltaEventData { Delta = String(update, "delta") ?? "", ProviderMetadata = Scoped(raw) }, metadata);
            }
            else if (kind == "text_end" && state.Texts.Remove(id)) yield return Event("text-end", id, new AITextEndEventData(), metadata);
            else if (kind == "thinking_end" && state.Reasoning.Remove(id)) yield return Event("reasoning-end", id, new AIReasoningEndEventData(), metadata);
        }
        else if (eventType == "tool_execution_start")
        {
            var id = String(item, "toolCallId"); var name = String(item, "toolName");
            if (id is null || name is null || Property(item, "args") is not { } args) yield break;
            state.ToolNames[id] = name;
            yield return Event("tool-input-available", id, new AIToolInputAvailableEventData { ToolName = name, Input = args, ProviderExecuted = true, ProviderMetadata = Scoped(raw) }, metadata);
        }
        else if (eventType == "tool_execution_end")
        {
            var id = String(item, "toolCallId");
            if (id is null) yield break;
            if (Property(item, "isError") is { ValueKind: JsonValueKind.True })
                yield return Event("tool-output-error", id, new AIToolOutputErrorEventData { ToolCallId = id, ErrorText = Property(item, "result")?.ToString() ?? "CamelAI tool failed.", ProviderExecuted = true, ProviderMetadata = Scoped(raw) }, metadata);
            else if (Property(item, "result") is { } output)
                yield return Event("tool-output-available", id, new AIToolOutputAvailableEventData { ToolName = state.ToolNames.GetValueOrDefault(id), Output = output, ProviderExecuted = true, ProviderMetadata = Scoped(raw) }, metadata);
        }
        else if (eventType == "file_presented" && String(item, "url") is { } url && state.Files.Add(url))
            yield return Event("file", frame.Cursor, new AIFileEventData { Url = url, Filename = String(item, "name"), MediaType = String(item, "contentType") ?? "application/octet-stream", ProviderMetadata = Scoped(raw) }, metadata);
    }
}
