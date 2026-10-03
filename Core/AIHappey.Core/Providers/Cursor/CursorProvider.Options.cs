using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Cursor;

public sealed partial class CursorProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private const string ReceiptTool = "cursor_agent_run";
    // Conversational adapter controls only. In particular, operation/query/endpoint-like fields are raw payload data.
    private static readonly HashSet<string> Controls = new(StringComparer.OrdinalIgnoreCase)
    { "body", "agentId", "lastEventId", "maxReconnects", "maxPolls" };
    private static JsonElement Element(object? value) => value is JsonElement el ? el : JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement? Prop(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in value.EnumerateObject())
            if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
    private static string? Str(JsonElement value, string key) => Prop(value, key) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
    private static int? Int(JsonElement value, string key) => Prop(value, key) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt32(out var i) ? i : null;
    private static JsonElement Empty => Element(new { });

    // Only inspect known request-envelope locations, never arbitrary history/tool payloads.
    public static JsonElement NormalizeOptions(AIRequest request)
    {
        var result = new JsonObject();
        var metadata = Element(request.Metadata);
        void Merge(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) return;
            foreach (var p in value.EnumerateObject()) result[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        }
        void Envelope(JsonElement value, int depth = 0)
        {
            if (depth > 4 || value.ValueKind != JsonValueKind.Object) return;
            foreach (var key in new[] { "additionalProperties", "metadata", "providerOptions", "provider_options", "options" })
                if (Prop(value, key) is { } nested) Envelope(nested, depth + 1);
            if (Prop(value, "cursor") is { } cursor) Merge(cursor);
        }
        foreach (var prefix in new[] { "chatcompletions", "messages", "responses", "vercel" })
            foreach (var suffix in new[] { "raw", "unmapped", "additionalProperties", "metadata", "providerOptions" })
                if (Prop(metadata, prefix + ".request." + suffix) is { } value) Envelope(value);
        Envelope(metadata);
        return Element(result);
    }

    private sealed record Route(string? AgentId, string? ModelId);
    private static Route ParseRoute(string? model)
    {
        var value = (model ?? "default").Trim();
        if (value.StartsWith("cursor/", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        if (value is "default" or "cursor" or "") return new(null, null);
        if (value.StartsWith("agents/", StringComparison.OrdinalIgnoreCase) && value.Length > 7) return new(value[7..], null);
        if (value.StartsWith("models/", StringComparison.OrdinalIgnoreCase) && value.Length > 7) return new(null, value[7..]);
        throw new ArgumentException("Cursor route must be cursor/default, cursor/models/{id}, or cursor/agents/{id}.");
    }
    private static string? Agree(string? left, string? right, string field)
    {
        if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && left != right)
            throw new ArgumentException($"Conflicting Cursor {field} in route/options.");
        return string.IsNullOrWhiteSpace(left) ? right : left;
    }
    private static string? ReceiptAgent(AIRequest request)
    {
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
                if (tool.ToolName == ReceiptTool) // Some protocol mappers drop ProviderExecuted on replay.
                {
                    foreach (var source in new[] { tool.Output, tool.Metadata, tool.Input })
                        if (source is not null && ReceiptId(Element(source)) is { } id) return id;
                }
        return null;
    }
    private static string? ReceiptId(JsonElement value, int depth = 0)
    {
        if (depth > 6 || value.ValueKind != JsonValueKind.Object) return null;
        if (Str(value, "agentId") is { Length: > 0 } id) return id;
        foreach (var key in new[] { "structuredContent", "cursor", "identity", "output", "metadata" })
            if (Prop(value, key) is { } nested && ReceiptId(nested, depth + 1) is { } found) return found;
        if (Prop(value, "content") is { ValueKind: JsonValueKind.Array } content)
            foreach (var part in content.EnumerateArray())
                if (Str(part, "text") is { } text)
                    try { if (ReceiptId(JsonDocument.Parse(text).RootElement, depth + 1) is { } found) return found; }
                    catch (JsonException) { }
        return null;
    }
    private static JsonObject Body(JsonElement options)
    {
        if (Prop(options, "body") is { } body)
            return body.ValueKind == JsonValueKind.Object ? JsonNode.Parse(body.GetRawText())!.AsObject()
                : throw new ArgumentException("Cursor body must be a JSON object.");
        var result = new JsonObject();
        foreach (var p in options.EnumerateObject())
            if (!Controls.Contains(p.Name)) result[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        return result;
    }

    private static JsonObject Prompt(AIRequest request)
    {
        var latest = request.Input?.Items?.LastOrDefault(i => i.Role?.Equals("user", StringComparison.OrdinalIgnoreCase) == true);
        var text = string.Join("\n", latest?.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
        if (string.IsNullOrWhiteSpace(text)) text = request.Input?.Text;
        var instructions = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) instructions.Add(request.Instructions);
        instructions.AddRange(request.Input?.Items?.Where(i => i.Role is "system" or "developer")
            .SelectMany(i => i.Content?.OfType<AITextContentPart>() ?? []).Select(p => p.Text) ?? []);
        if (instructions.Count > 0) text = string.Join("\n", instructions.Distinct()) + "\n\n" + text;
        var images = new JsonArray();
        foreach (var part in latest?.Content ?? [])
        {
            if (part is AITextContentPart) continue;
            if (part is not AIFileContentPart file) throw new NotSupportedException("Cursor user attachments must be supported images or text.");
            if (images.Count == 5) throw new ArgumentException("Cursor allows at most five images per prompt.");
            images.Add(Image(file));
        }
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0) throw new ArgumentException("Cursor requires a latest user prompt or image.");
        var result = new JsonObject { ["text"] = text ?? "" };
        if (images.Count > 0) result["images"] = images;
        return result;
    }

    private static JsonObject Image(AIFileContentPart file)
    {
        var mime = file.MediaType?.ToLowerInvariant();
        var value = file.Data is byte[] bytes ? Convert.ToBase64String(bytes) : file.Data?.ToString()?.Trim();
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Cursor image has no data.");
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Cursor requires base64 image data URLs.");
            var dataMime = value[5..value.IndexOf(';')].ToLowerInvariant();
            if (mime is not null && mime != dataMime) throw new ArgumentException("Cursor image MIME conflicts with data URL.");
            mime = dataMime; value = value[(comma + 1)..];
        }
        if (mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
            throw new NotSupportedException("Cursor supports PNG, JPEG, GIF and WebP images only; specify the image MIME type.");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return new JsonObject { ["url"] = value };
        byte[] decoded;
        try { decoded = Convert.FromBase64String(value); }
        catch (FormatException) { throw new ArgumentException("Cursor image contains invalid base64."); }
        if (decoded.Length > 15 * 1024 * 1024) throw new ArgumentException("Cursor images must not exceed 15 MB each.");
        return new JsonObject { ["data"] = Convert.ToBase64String(decoded), ["mimeType"] = mime };
    }
}
