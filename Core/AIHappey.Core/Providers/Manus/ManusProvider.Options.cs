using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Manus;

public sealed partial class ManusProvider
{
    private const string ReceiptTool = "manus_task";
    private const string FormPrefix = "manus-input-";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static JsonElement El(object? value) => value is JsonElement el ? el.Clone() : JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement Empty => El(new { });
    private static JsonElement? P(JsonElement el, string key) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var p) ? p : null;
    private static string? S(JsonElement el, string key) => P(el, key) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
    private static bool? B(JsonElement el, string key) => P(el, key)?.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static int? N(JsonElement el, string key) => P(el, key) is { } p && p.TryGetInt32(out var n) ? n : null;
    private static JsonElement[] Array(JsonElement el, string key) => P(el, key) is { ValueKind: JsonValueKind.Array } p ? p.EnumerateArray().ToArray() : [];
    private static JsonObject Obj(JsonElement el) => el.ValueKind == JsonValueKind.Object ? JsonNode.Parse(el.GetRawText())!.AsObject() : throw new ArgumentException("Manus conversational options must be objects.");
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    { "operation", "method", "path", "endpoint", "url", "baseUrl", "host", "headers", "authorization", "apiKey", "x-manus-api-key", "confirmation", "confirmAction", "event_id", "input" };
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal)
    { "body", "task_id", "pollOnly", "start_event_id", "pollIntervalMs", "timeoutSeconds", "maxPolls", "verbose", "slides_format" };

    private static JsonElement Options(AIRequest request)
    {
        var result = new JsonObject();
        void Envelope(JsonElement value, int depth = 0)
        {
            if (depth > 4 || value.ValueKind != JsonValueKind.Object) return;
            foreach (var name in new[] { "additionalProperties", "metadata", "providerOptions", "provider_options", "options" })
                if (P(value, name) is { } nested) Envelope(nested, depth + 1);
            if (P(value, "manus") is { } options)
                foreach (var p in Obj(options)) result[p.Key] = p.Value?.DeepClone();
        }
        var metadata = El(request.Metadata);
        foreach (var prefix in new[] { "chatcompletions", "messages", "responses", "vercel" })
            foreach (var suffix in new[] { "raw", "unmapped", "additionalProperties", "metadata", "providerOptions" })
                if (P(metadata, prefix + ".request." + suffix) is { } value) Envelope(value);
        Envelope(metadata);
        CheckSelectors(result);
        if (result["body"] is JsonObject body) CheckSelectors(body);
        else if (result.ContainsKey("body")) throw new ArgumentException("Manus body must be an object.");
        return El(result);
    }
    private static void CheckSelectors(JsonObject obj)
    {
        foreach (var key in obj.Select(p => p.Key))
            if (Forbidden.Contains(key) || key.Equals("query", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Manus conversational options do not allow {key}; endpoint selection and confirmation commands are not supported.");
    }
    private static JsonObject Body(JsonElement options)
    {
        var body = new JsonObject();
        foreach (var p in options.EnumerateObject())
            if (!Controls.Contains(p.Name)) body[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        if (P(options, "body") is { } raw)
            foreach (var p in Obj(raw)) body[p.Key] = p.Value?.DeepClone();
        return body;
    }
    private sealed record Route(string Model, string? Profile, string? AgentId);
    private static Route ParseRoute(string? model)
    {
        var value = model ?? "standard";
        if (value.StartsWith("manus/", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        if (value is "default" or "" or "manus") value = "standard";
        if (value is "standard" or "lite" or "max") return new("manus/" + value, value, null);
        if (value.StartsWith("agents/", StringComparison.Ordinal) && value.Length > 7 && !value[7..].Contains('/'))
            return new("manus/" + value, null, value[7..]);
        throw new ArgumentException("Manus model must select standard, lite, max, or agents/{agentId}.");
    }
    private static string? Agree(string? a, string? b, string field)
    {
        if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b) throw new ArgumentException($"Conflicting Manus {field}.");
        return string.IsNullOrEmpty(a) ? b : a;
    }
    private static JsonElement Unwrap(object? value)
    {
        var el = El(value);
        if (el.ValueKind == JsonValueKind.String)
            try { using var doc = JsonDocument.Parse(el.GetString()!); el = doc.RootElement.Clone(); } catch (JsonException) { }
        if (P(el, "structuredContent") is { } structured) return structured;
        foreach (var part in Array(el, "content"))
            if (S(part, "text") is { } text)
                try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); } catch (JsonException) { }
        return el;
    }
    private static JsonElement? Receipt(AIRequest request)
    {
        foreach (var tool in request.Input?.Items?.SelectMany(i => i.Content ?? []).OfType<AIToolCallContentPart>().Reverse() ?? [])
            if (tool.ToolName == ReceiptTool && Unwrap(tool.Output ?? tool.Input) is var receipt && S(receipt, "task_id") is not null)
                return receipt;
        return null;
    }
    private static AIToolCallContentPart? Answer(AIRequest request)
    {
        var items = request.Input?.Items ?? [];
        var lastUser = items.FindLastIndex(i => i.Role == "user");
        return items.SelectMany((item, index) => (item.Content ?? []).OfType<AIToolCallContentPart>().Select(tool => (tool, index)))
            .LastOrDefault(x => x.index >= lastUser && x.tool.ToolName == "ai_input_required" && x.tool.ToolCallId.StartsWith(FormPrefix, StringComparison.Ordinal) && x.tool.Output is not null).tool;
    }
}
