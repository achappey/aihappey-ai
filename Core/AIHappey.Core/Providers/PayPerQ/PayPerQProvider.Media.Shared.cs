using System.Text.Json;

namespace AIHappey.Core.Providers.PayPerQ;

public partial class PayPerQProvider
{
    private static Dictionary<string, object?> CopyPayPerQOptions(Dictionary<string, JsonElement>? options)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options is null) return result;
        foreach (var (name, value) in options)
            if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                result[name] = value.Clone();
        return result;
    }

    private static string PayPerQJsonText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static string? PayPerQGetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static float? PayPerQGetFloat(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.TryGetSingle(out var number) ? number : null;

    private static long PayPerQCreated(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("created", out var value)
            && value.TryGetInt64(out var created) ? created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static byte[] DecodePayPerQBase64(object value)
    {
        var text = value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Media data is required.", nameof(value));
        var comma = text.IndexOf(',');
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) text = text[(comma + 1)..];
        try { return Convert.FromBase64String(text); }
        catch (FormatException exception) { throw new ArgumentException("Media data must be valid base64.", nameof(value), exception); }
    }

    private async Task<PayPerQMedia> DownloadPayPerQMediaAsync(string value, bool video, CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var semicolon = value.IndexOf(';');
            return new PayPerQMedia(Convert.ToBase64String(DecodePayPerQBase64(value)),
                semicolon > 5 ? value[5..semicolon] : video ? "video/mp4" : "image/png");
        }

        using var response = await _client.GetAsync(value, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"PayPerQ media download failed ({(int)response.StatusCode}).");
        return new PayPerQMedia(Convert.ToBase64String(bytes),
            response.Content.Headers.ContentType?.MediaType ?? GuessPayPerQMediaType(value, video));
    }

    private static string GuessPayPerQMediaType(string value, bool video)
    {
        var path = value.Split('?', '#')[0].ToLowerInvariant();
        if (video) return path.EndsWith(".webm") ? "video/webm" : path.EndsWith(".mov") ? "video/quicktime" : "video/mp4";
        return path.EndsWith(".jpg") || path.EndsWith(".jpeg") ? "image/jpeg" : path.EndsWith(".webp") ? "image/webp" : "image/png";
    }

    private static void EnsurePayPerQSuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPerQ {operation} failed ({(int)response.StatusCode}): {raw}");
    }

    private sealed record PayPerQMedia(string Base64, string MediaType, string? RevisedPrompt = null);
}
