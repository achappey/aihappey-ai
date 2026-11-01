using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.BaguetteBot;

public sealed partial class BaguetteBotProvider
{
    private sealed record Frame(string Type, JsonElement? Data);
    private sealed class TextState
    {
        public StringBuilder Text { get; } = new();
        public bool Ended { get; set; }
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = BuildCall(request);
        if (call.Operation != "messages")
            throw new NotSupportedException("BaguetteBot native SSE is available only for agent messages. Use non-streaming execution for explicit operations.");
        call.Payload["wait"] = false;
        using var timeout = CreateTimeout(call, cancellationToken);
        var ct = timeout.Token;
        await ValidateAgentAsync(call, ct);
        await UploadFilesAsync(call, ct);

        // Subscribe before dispatch: a short task can finish before POST returns its 202.
        using var eventsRequest = CreateRequest(HttpMethod.Get, AgentPath(call) + "/events");
        eventsRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(eventsRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new InvalidOperationException("BaguetteBot events endpoint did not return text/event-stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await using var frames = ReadFramesAsync(reader, ct).GetAsyncEnumerator(ct);
        JsonElement ready;
        while (true)
        {
            if (!await frames.MoveNextAsync())
                throw new IOException("BaguetteBot events stream closed before ready; no task was submitted.");
            if (frames.Current.Type != "ready")
                continue;
            ready = frames.Current.Data ?? throw new InvalidOperationException("BaguetteBot ready event has no data.");
            if (RequiredString(ready, "agent_id") != call.AgentId)
                throw new InvalidOperationException("BaguetteBot ready event returned a different agent ID; no task was submitted.");
            break;
        }

        var run = await SendJsonAsync(HttpMethod.Post, AgentPath(call) + "/messages", call.Payload, ct);
        ValidateRun(run, call);
        yield return NativeEvent(call, "ready", ready);
        yield return NativeEvent(call, "run", run);
        var texts = new Dictionary<string, TextState>(StringComparer.Ordinal);
        JsonElement? completed = Text(run, "status") == "done" ? run : null;

        while (!completed.HasValue && await frames.MoveNextAsync())
        {
            var frame = frames.Current;
            if (frame.Data is { } raw)
            {
                if (frame.Type == "delta")
                {
                    var messageId = RequiredString(raw, "message_id");
                    var delta = Text(raw, "text") ?? throw new InvalidOperationException("BaguetteBot delta is missing text.");
                    if (delta.Length > 0)
                        foreach (var part in AppendText(call, texts, messageId, delta, raw))
                            yield return part;
                }
                else
                {
                    // Native tool summaries have no call IDs or arguments. Keep them intact
                    // instead of manufacturing correlated tool-input/tool-output pairs.
                    yield return NativeEvent(call, frame.Type, raw);
                    if (frame.Type is "message" or "message.updated"
                        && Text(raw, "role") == "assistant" && Text(raw, "content") is { } text)
                    {
                        var id = RequiredString(raw, "id");
                        foreach (var part in CompleteText(call, texts, id, text, raw, frame.Type == "message.updated"))
                            yield return part;
                    }
                }
            }
            if (frame.Type == ":comment" || frame.Type == "status" && Text(frame.Data ?? default, "status") is "idle" or "blocked")
                completed = await TryCompleteAsync(call, run, ct);
        }

        completed ??= await TryCompleteAsync(call, run, ct);
        if (!completed.HasValue)
            throw new IOException($"BaguetteBot events stream closed while agent {call.AgentId} was working; message_id={RequiredString(run, "message_id")}. The remote task was not stopped.");
        var final = completed.Value;
        if (Property(final, "messages") is { ValueKind: JsonValueKind.Array } messages)
        {
            yield return NativeEvent(call, "history", JsonSerializer.SerializeToElement(new { @object = "list", data = messages }, Json));
            foreach (var message in messages.EnumerateArray())
            {
                if (Text(message, "role") == "assistant" && Text(message, "content") is { } text)
                    foreach (var part in CompleteText(call, texts, RequiredString(message, "id"), text, message, end: true))
                        yield return part;
            }
        }
        if (texts.Count == 0 && Text(final, "output") is { Length: > 0 } output)
            foreach (var part in CompleteText(call, texts, "output-" + RequiredString(run, "message_id"), output, final, end: true))
                yield return part;
        foreach (var (id, state) in texts.Where(entry => !entry.Value.Ended))
        {
            state.Ended = true;
            yield return Event(call, "text-end", id, new AITextEndEventData(), final);
        }

        var mapped = await MapRunAsync(request, call, final, ct);
        foreach (var file in mapped.Output?.Items?.SelectMany(item => item.Content ?? []).OfType<AIFileContentPart>() ?? [])
            yield return Event(call, "file", file.Filename, new AIFileEventData
            {
                MediaType = file.MediaType!, Url = (string)file.Data!, Filename = file.Filename
            }, final);
        if (Text(final, "status") == "blocked")
            yield return Event(call, "error", RequiredString(run, "message_id"), new AIErrorEventData
            {
                ErrorText = Text(Property(final, "agent") ?? default, "status_text") ?? $"BaguetteBot agent {call.AgentId} is blocked."
            }, final);
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(), Metadata = mapped.Metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish", Id = RequiredString(run, "message_id"), Timestamp = DateTimeOffset.UtcNow,
                Output = mapped.Output, Metadata = mapped.Metadata,
                Data = new AIFinishEventData
                {
                    FinishReason = Text(final, "status") == "done" ? "stop" : "other", Model = request.Model,
                    Response = final.Clone(), CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(request.Model!, DateTimeOffset.UtcNow,
                        additionalProperties: mapped.Metadata)
                }
            }
        };
    }

    private IEnumerable<AIStreamEvent> AppendText(Call call, Dictionary<string, TextState> texts,
        string id, string delta, JsonElement raw)
    {
        if (!texts.TryGetValue(id, out var state))
        {
            texts[id] = state = new TextState();
            yield return Event(call, "text-start", id, new AITextStartEventData { ProviderMetadata = Scoped(call, raw) }, raw);
        }
        if (state.Ended)
            throw new InvalidOperationException($"BaguetteBot sent a delta after message {id} was completed.");
        state.Text.Append(delta);
        yield return Event(call, "text-delta", id, new AITextDeltaEventData { Delta = delta, ProviderMetadata = Scoped(call, raw) }, raw);
    }

    private IEnumerable<AIStreamEvent> CompleteText(Call call, Dictionary<string, TextState> texts,
        string id, string text, JsonElement raw, bool end)
    {
        var existing = texts.TryGetValue(id, out var state) ? state.Text.ToString() : "";
        if (state?.Ended == true)
            yield break;
        // Updates are snapshots, not deltas. Revisions remain in native metadata;
        // only a genuinely missing suffix can be appended to a Unified text stream.
        if (text.StartsWith(existing, StringComparison.Ordinal) && text.Length > existing.Length)
            foreach (var part in AppendText(call, texts, id, text[existing.Length..], raw))
                yield return part;
        if (end && texts.TryGetValue(id, out state) && !state.Ended)
        {
            state.Ended = true;
            yield return Event(call, "text-end", id, new AITextEndEventData { ProviderMetadata = Scoped(call, raw) }, raw);
        }
    }

    private static Dictionary<string, object> Scoped(Call call, JsonElement raw)
        => new() { ["baguettebot"] = new { agent_id = call.AgentId, raw = raw.Clone() } };

    private AIStreamEvent NativeEvent(Call call, string type, JsonElement raw)
        => Event(call, "data-baguettebot-" + type.Replace('.', '-'), Text(raw, "id") ?? Text(raw, "message_id"),
            new AIDataEventData { Data = raw.Clone(), Transient = false }, raw);

    private AIStreamEvent Event(Call call, string type, string? id, object data, JsonElement raw)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = Metadata(call, raw),
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = Metadata(call, raw) }
        };

    private static async IAsyncEnumerable<Frame> ReadFramesAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string type = "message";
        var data = new StringBuilder();
        var comment = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    yield return new Frame(type, document.RootElement.Clone());
                }
                else if (comment)
                    yield return new Frame(":comment", null);
                type = "message";
                data.Clear();
                comment = false;
                continue;
            }
            if (line.StartsWith(':'))
            {
                comment = true;
                continue;
            }
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            if (field == "event")
                type = value;
            else if (field == "data")
                data.Append(value).Append('\n');
        }
        // An unterminated frame at EOF is not an SSE event.
    }
}
