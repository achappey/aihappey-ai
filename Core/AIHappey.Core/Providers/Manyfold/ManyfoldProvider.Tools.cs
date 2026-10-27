using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Manyfold;

public partial class ManyfoldProvider
{
    // Manyfold's agent tools are already executed upstream. Only its line-oriented
    // transcript records are translated here; they are never gateway tool requests.
    private sealed class ToolTranscript
    {
        private const string CallPrefix = "[tool_call:";
        private const string ResultPrefix = "[tool_result:";
        private readonly string id = $"manyfold-tool-{Guid.NewGuid():N}";
        private readonly Queue<TranscriptTool> pending = new();
        private string buffer = "";
        private bool lineWhitespace = true;
        private int ordinal;

        public bool HasTools => ordinal > 0;
        public bool TextActive { get; set; }
        public string? TextId { get; set; }
        public int TextOrdinal { get; set; }

        public IEnumerable<TranscriptEntry> Read(string text, bool final = false)
        {
            buffer += text;
            while (buffer.Length > 0)
            {
                var start = FindMarker();
                if (start < 0)
                {
                    var count = buffer.Length;
                    if (!final)
                    {
                        // Keep a possible split header and its separator, not the answer.
                        for (var length = 1; length < ResultPrefix.Length && length <= buffer.Length; length++)
                            if (CallPrefix.StartsWith(buffer[^length..], StringComparison.Ordinal)
                                || ResultPrefix.StartsWith(buffer[^length..], StringComparison.Ordinal))
                                count = Math.Min(count, buffer.Length - length);
                        while (count > 0 && char.IsWhiteSpace(buffer[count - 1])) count--;
                    }
                    if (count == 0) yield break;
                    yield return new(Consume(count), null, false);
                    continue;
                }

                var isCall = buffer.AsSpan(start).StartsWith(CallPrefix, StringComparison.Ordinal);
                var prefix = isCall ? CallPrefix : ResultPrefix;
                var close = buffer.IndexOf(']', start + prefix.Length);
                var payloadStart = close + 1;
                if (close < 0)
                {
                    if (!final && !buffer.AsSpan(start).Contains('\n')) yield break;
                }
                else
                {
                    while (payloadStart < buffer.Length && char.IsWhiteSpace(buffer[payloadStart])) payloadStart++;
                    var name = buffer[(start + prefix.Length)..close];
                    if (name.Length > 0 && !name.Any(char.IsWhiteSpace)
                        && (isCall || pending.Count > 0))
                    {
                        var bytes = Encoding.UTF8.GetBytes(buffer[payloadStart..]);
                        var reader = new Utf8JsonReader(bytes, isFinalBlock: final, state: default);
                        JsonDocument? document = null;
                        var incomplete = false;
                        try { incomplete = !JsonDocument.TryParseValue(ref reader, out document); }
                        catch (JsonException) { /* Unknown/malformed records remain ordinary text. */ }
                        if (incomplete && !final) yield break;
                        if (document is not null)
                        {
                            using (document)
                            {
                                var value = document.RootElement.Clone();
                                if (!isCall || value.ValueKind == JsonValueKind.Object)
                                {
                                    var end = payloadStart + Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)reader.BytesConsumed));
                                    // Newlines before a record are protocol separators. Holding
                                    // trailing whitespace above makes this independent of chunks.
                                    var before = Consume(start).TrimEnd();
                                    if (!string.IsNullOrWhiteSpace(before)) yield return new(before, null, false);
                                    Consume(end - start);
                                    if (isCall)
                                    {
                                        var tool = new TranscriptTool($"{id}-{++ordinal}", name, value);
                                        pending.Enqueue(tool);
                                        yield return new(null, tool, false);
                                    }
                                    else
                                    {
                                        // Call records contain a name but no ID. Manyfold emits
                                        // their result records in execution order, with the ID.
                                        var tool = pending.Dequeue();
                                        tool.Output = value;
                                        tool.ResultId = name;
                                        yield return new(null, tool, true);
                                    }
                                    continue;
                                }
                            }
                        }
                    }
                }
                // Make progress without discarding any bytes of an unsupported record.
                yield return new(Consume(start + 1), null, false);
            }
        }

        private int FindMarker()
        {
            var whitespace = lineWhitespace;
            for (var i = 0; i < buffer.Length; i++)
            {
                if (whitespace && (buffer.AsSpan(i).StartsWith(CallPrefix, StringComparison.Ordinal)
                    || buffer.AsSpan(i).StartsWith(ResultPrefix, StringComparison.Ordinal))) return i;
                whitespace = buffer[i] == '\n' || (whitespace && char.IsWhiteSpace(buffer[i]));
            }
            return -1;
        }

        private string Consume(int count)
        {
            var text = buffer[..count];
            foreach (var ch in text)
                lineWhitespace = ch == '\n' || (lineWhitespace && char.IsWhiteSpace(ch));
            buffer = buffer[count..];
            return text;
        }
    }

    private sealed record TranscriptEntry(string? Text, TranscriptTool? Tool, bool IsOutput);

    private sealed class TranscriptTool(string id, string name, JsonElement input)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public JsonElement Input { get; } = input;
        public JsonElement? Output { get; set; }
        public string? ResultId { get; set; }

        public Dictionary<string, Dictionary<string, object>> Scope() => new()
        {
            ["manyfold"] = new() { ["tool_name"] = Name, ["synthetic_tool"] = true,
                ["tool_call_id"] = ResultId ?? Id }
        };

        public AIToolCallContentPart Part() => new()
        {
            Type = "tool-call", ToolCallId = Id, ToolName = Name, Title = Name, Input = Input, Output = Output,
            ProviderExecuted = true, State = Output.HasValue ? "output-available" : "input-available",
            Metadata = new() { ["manyfold"] = Scope()["manyfold"] }
        };
    }

    private IEnumerable<AIStreamEvent> TranscriptEvents(ToolTranscript transcript, string text,
        string textId, Dictionary<string, object?> metadata, bool final = false)
    {
        foreach (var entry in transcript.Read(text, final))
        {
            if (entry.Tool is not { } tool)
            {
                if (transcript.HasTools && !transcript.TextActive && string.IsNullOrWhiteSpace(entry.Text)) continue;
                if (!transcript.TextActive)
                {
                    transcript.TextActive = true;
                    transcript.TextId = $"{textId}-{++transcript.TextOrdinal}";
                    yield return Event("text-start", transcript.TextId, new AITextStartEventData(), metadata);
                }
                yield return Event("text-delta", transcript.TextId, new AITextDeltaEventData { Delta = entry.Text! }, metadata);
                continue;
            }
            if (transcript.TextActive)
            {
                yield return Event("text-end", transcript.TextId, new AITextEndEventData(), metadata);
                transcript.TextActive = false;
            }
            if (entry.IsOutput)
                yield return Event("tool-output-available", tool.Id, new AIToolOutputAvailableEventData
                {
                    ToolName = tool.Name, Output = tool.Output!.Value, ProviderExecuted = true, ProviderMetadata = tool.Scope()
                }, metadata);
            else
                yield return Event("tool-input-available", tool.Id, new AIToolInputAvailableEventData
                {
                    ToolName = tool.Name, Title = tool.Name, Input = tool.Input, ProviderExecuted = true, ProviderMetadata = tool.Scope()
                }, metadata);
        }
    }

    private static void ConvertToolTranscripts(List<AIOutputItem> items, JsonArray? choices)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Role != "assistant") continue;
            var content = new List<AIContentPart>();
            var addedTools = new List<AIToolCallContentPart>();
            foreach (var part in item.Content ?? [])
            {
                if (part is not AITextContentPart text) { content.Add(part); continue; }
                var entries = new ToolTranscript().Read(text.Text, final: true).ToList();
                if (!entries.Any(e => e.Tool is not null)) { content.Add(part); continue; }
                foreach (var entry in entries)
                {
                    if (entry.Tool is { } tool)
                    {
                        if (entry.IsOutput) continue;
                        var toolPart = tool.Part();
                        content.Add(toolPart);
                        addedTools.Add(toolPart);
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.Text))
                        content.Add(new AITextContentPart { Type = "text", Text = entry.Text! });
                }
            }
            if (addedTools.Count == 0) continue;
            items[i] = new AIOutputItem { Type = item.Type, Role = item.Role, Metadata = item.Metadata, Content = content };
            // Keep native extra fields and choices, but don't let the protocol mapper
            // bypass the normalized text/tools by replaying the original choices.
            if (choices is not null && i < choices.Count && choices[i]?["message"] is JsonObject message)
            {
                message["content"] = string.Concat(content.OfType<AITextContentPart>().Select(p => p.Text));
                var calls = message["tool_calls"] as JsonArray ?? new JsonArray();
                if (message["tool_calls"] is null) message["tool_calls"] = calls;
                foreach (var tool in addedTools) calls.Add(NativeToolCall(tool));
            }
        }
    }

    private static JsonNode NativeToolCall(AIToolCallContentPart tool)
    {
        var call = JsonSerializer.SerializeToNode(new
        {
            id = tool.ToolCallId, type = "function", provider_executed = true,
            function = new { name = tool.ToolName, arguments = JsonSerializer.Serialize(tool.Input, Json), output = tool.Output }
        }, Json)!;
        // An observed input is not proof of completion if its result was truncated.
        if (tool.Output is null) call["function"]!.AsObject().Remove("output");
        return call;
    }
}
