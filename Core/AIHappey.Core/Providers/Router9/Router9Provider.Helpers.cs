using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;

namespace AIHappey.Core.Providers.Router9;

public partial class Router9Provider
{
    private static readonly JsonSerializerOptions Router9JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<Router9JsonResult> SendRouter9JsonAsync(
        string relativeUrl,
        JsonObject payload,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.PostAsJsonAsync(relativeUrl, payload, Router9JsonOptions, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Router9 {operation} failed ({(int)response.StatusCode}): {json}");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException($"Router9 {operation} failed: {json}");
            return new Router9JsonResult(root, GetRouter9Headers(response));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Router9 {operation} returned invalid JSON.", ex);
        }
    }

    private static Dictionary<string, string> GetRouter9Headers(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);

    private static (byte[] Bytes, string MediaType) DecodeRouter9Data(object data, string fallbackMediaType)
    {
        var value = data switch
        {
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio must be a base64 string or data URL.", nameof(data));

        var mediaType = string.IsNullOrWhiteSpace(fallbackMediaType) ? "audio/mpeg" : fallbackMediaType;
        var comma = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 5)
        {
            var metadata = value[5..comma];
            var semicolon = metadata.IndexOf(';');
            mediaType = semicolon >= 0 ? metadata[..semicolon] : metadata;
            value = value[(comma + 1)..];
        }

        try { return (Convert.FromBase64String(value), mediaType); }
        catch (FormatException ex) { throw new ArgumentException("Audio contains invalid base64 data.", nameof(data), ex); }
    }

    private static JsonObject CreateRouter9Payload(Dictionary<string, JsonElement>? additionalProperties, params string[] excluded)
    {
        var payload = new JsonObject();
        var excludedSet = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in additionalProperties ?? [])
            if (!excludedSet.Contains(property.Key)) payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
        return payload;
    }

    private static string? GetRouter9String(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private sealed record Router9JsonResult(JsonElement Root, Dictionary<string, string> Headers);
}
