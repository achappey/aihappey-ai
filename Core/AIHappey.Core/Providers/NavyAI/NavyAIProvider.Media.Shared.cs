using System.Text.Json;

namespace AIHappey.Core.Providers.NavyAI;

public partial class NavyAIProvider
{
    private static Dictionary<string, object?> NavyCopyOptions(Dictionary<string, JsonElement>? options)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options is null) return result;
        foreach (var (name, value) in options)
            if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                result[name] = value.Clone();
        return result;
    }

    private static string NavyJsonText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static string? NavyGetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? NavyGetFloat(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.TryGetSingle(out var number) ? number : null;

    private static string NavyRemoveDataUrlPrefix(string value)
    {
        var comma = value.IndexOf(',');
        return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0 ? value[(comma + 1)..] : value;
    }

    private static byte[] NavyDecodeBase64(object value)
    {
        var text = value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Audio is required.", nameof(value));
        try { return Convert.FromBase64String(NavyRemoveDataUrlPrefix(text)); }
        catch (FormatException exception) { throw new ArgumentException("Audio must be valid base64.", nameof(value), exception); }
    }

    private static string? NavyJobId(JsonElement root)
    {
        foreach (var name in new[] { "id", "job_id", "task_id" })
            if (!string.IsNullOrWhiteSpace(NavyGetString(root, name))) return NavyGetString(root, name);
        return null;
    }

    private static string? NavyStatus(JsonElement root)
        => NavyGetString(root, "status") ?? NavyGetString(root, "state");

    private static bool NavyIsFailure(JsonElement root)
        => NavyStatus(root)?.Trim().ToLowerInvariant() is "failed" or "error" or "cancelled" or "canceled";

    private static bool NavyIsTerminal(JsonElement root)
        => NavyHasMediaData(root) || NavyIsFailure(root)
            || NavyStatus(root)?.Trim().ToLowerInvariant() is "completed" or "complete" or "succeeded" or "success" or "done";

    private static bool NavyHasMediaData(JsonElement root)
    {
        var data = NavyFindData(root);
        if (data.ValueKind == JsonValueKind.Array) return data.GetArrayLength() > 0;
        return data.ValueKind == JsonValueKind.Object && (NavyGetString(data, "url") is not null || NavyGetString(data, "b64_json") is not null);
    }

    private static JsonElement NavyFindData(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) return data;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out data)) return data;
            return result;
        }
        return default;
    }

    private async Task<List<NavyMedia>> ResolveNavyMediaAsync(JsonElement root, bool video, CancellationToken cancellationToken)
    {
        var result = new List<NavyMedia>();
        var data = NavyFindData(root);
        IEnumerable<JsonElement> values = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray()
            : data.ValueKind == JsonValueKind.Object ? [data] : [];
        foreach (var item in values)
        {
            var base64 = NavyGetString(item, "b64_json") ?? NavyGetString(item, "base64");
            var url = NavyGetString(item, "url") ?? NavyGetString(item, "video_url") ?? NavyGetString(item, "output_url");
            var revised = NavyGetString(item, "revised_prompt");
            if (!string.IsNullOrWhiteSpace(base64))
            {
                result.Add(new NavyMedia(NavyRemoveDataUrlPrefix(base64), video ? "video/mp4" : "image/png", revised));
                continue;
            }
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var semicolon = url.IndexOf(';');
                result.Add(new NavyMedia(NavyRemoveDataUrlPrefix(url), semicolon > 5 ? url[5..semicolon] : video ? "video/mp4" : "image/png", revised));
                continue;
            }
            using var response = await _client.GetAsync(url, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"NavyAI media download failed ({(int)response.StatusCode}).");
            result.Add(new NavyMedia(Convert.ToBase64String(bytes), response.Content.Headers.ContentType?.MediaType
                ?? NavyGuessMediaType(url, video), revised));
        }
        return result;
    }

    private static DateTime NavyCreated(JsonElement root)
        => root.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;

    private static string NavyGuessMediaType(string url, bool video)
    {
        var path = url.Split('?', '#')[0].ToLowerInvariant();
        if (video) return path.EndsWith(".webm") ? "video/webm" : path.EndsWith(".mov") ? "video/quicktime" : "video/mp4";
        return path.EndsWith(".jpg") || path.EndsWith(".jpeg") ? "image/jpeg" : path.EndsWith(".webp") ? "image/webp" : "image/png";
    }

    private static void NavyEnsureSuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NavyAI {operation} failed ({(int)response.StatusCode}): {raw}");
    }
}
