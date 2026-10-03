using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Manus;

public sealed partial class ManusProvider
{
    private static async Task<JsonObject> Message(Execution e, bool initial, CancellationToken ct)
    {
        var message = e.Body["message"] is JsonObject raw ? (JsonObject)raw.DeepClone() : new JsonObject();
        // Explicit provider content is an escape hatch for native file IDs/visibility and future content fields.
        if (message.ContainsKey("content")) return message;
        var parts = new JsonArray();
        var instructions = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.Request.Instructions)) instructions.Add(e.Request.Instructions);
        instructions.AddRange(e.Request.Input?.Items?.Where(i => i.Role is "system" or "developer")
            .SelectMany(i => i.Content?.OfType<AITextContentPart>() ?? []).Select(p => p.Text) ?? []);
        if (instructions.Count > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = string.Join("\n", instructions.Distinct()) });
        var items = e.Request.Input?.Items ?? [];
        var latest = items.LastOrDefault(i => i.Role == "user");
        var selected = initial ? items.Where(i => i.Role is "user" or "assistant") : latest is null ? [] : new[] { latest };
        foreach (var item in selected)
            foreach (var part in item.Content ?? [])
            {
                if (part is AITextContentPart text)
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = initial && items.Count > 1 ? item.Role + ": " + text.Text : text.Text });
                else if (part is AIFileContentPart file && item.Role == "user") parts.Add(await FilePart(e.Api, file, ct));
                else if (part is not AIToolCallContentPart && item.Role == "user") throw new NotSupportedException("Unsupported Manus input content.");
            }
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(e.Request.Input?.Text))
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = e.Request.Input.Text });
        if (parts.Count == 0) throw new ArgumentException("Manus requires a user message or attachment.");
        message["content"] = parts;
        return message;
    }

    private static async Task<JsonObject> FilePart(ManusApiClient api, AIFileContentPart file, CancellationToken ct)
    {
        var mime = file.MediaType ?? "application/octet-stream";
        var filename = file.Filename;
        var value = file.Data is byte[] ? null : file.Data?.ToString();
        var result = new JsonObject { ["type"] = mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "voice" : "file", ["mime_type"] = mime };
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            result["file_url"] = value;
            if (filename is not null) result["filename"] = filename;
            return result;
        }
        byte[] bytes;
        if (file.Data is byte[] data) bytes = data;
        else
        {
            if (value?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
            {
                var comma = value.IndexOf(',');
                var semi = value.IndexOf(';');
                if (comma < 0 || semi < 5 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Manus requires base64 data URLs.");
                var actual = value[5..semi];
                if (file.MediaType is not null && !actual.Equals(mime, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Attachment MIME mismatch.");
                mime = actual; result["mime_type"] = mime; value = value[(comma + 1)..];
            }
            try { bytes = Convert.FromBase64String(value ?? ""); } catch (FormatException) { throw new ArgumentException("Invalid Manus attachment data."); }
        }
        if (bytes.Length == 0) throw new ArgumentException("Empty Manus attachment.");
        filename ??= "attachment" + (mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "application/pdf" => ".pdf", "audio/wav" => ".wav", "audio/mpeg" => ".mp3", _ => ".bin" });
        result["filename"] = filename;
        if (bytes.Length > 20 * 1024 * 1024) result["file_id"] = await api.Upload(filename, bytes, ct);
        else result["file_data"] = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        return result;
    }

    private static JsonNode? StructuredSchema(AIRequest request)
    {
        if (request.ResponseFormat is null) return null;
        var format = El(request.ResponseFormat);
        var type = S(format, "type");
        if (type is null or "text") return null;
        if (type != "json_schema") throw new NotSupportedException("Manus structured output requires a JSON schema, not json_object.");
        var schema = P(format, "schema") ?? (P(format, "json_schema") is { } nested ? P(nested, "schema") : null);
        return schema is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText()) : throw new ArgumentException("Missing Manus structured output schema.");
    }

    private static bool DownloadUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
    private static string ResolveLinks(string text, JsonElement[] attachments)
        => Regex.Replace(text, @"!?\[[^\]]*\]\((?<destination>[^\s)]+)(?:\s+""[^""]*"")?\)", match =>
        {
            var destination = match.Groups["destination"].Value;
            if (DownloadUrl(destination)) return match.Value;
            string decoded;
            try { decoded = Uri.UnescapeDataString(destination); } catch (UriFormatException) { return ""; }
            var url = attachments.Where(a => S(a, "path") == decoded).Select(a => S(a, "url")).FirstOrDefault(DownloadUrl);
            return url is null ? "" : match.Value.Replace(destination, url, StringComparison.Ordinal);
        });
}
