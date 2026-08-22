using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.TokenLab;

public partial class TokenLabProvider
{
    private static readonly JsonSerializerOptions TokenLabJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private JsonElement? GetTokenLabProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || !providerOptions.TryGetValue(GetIdentifier(), out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"providerOptions.{GetIdentifier()} must be a JSON object.", nameof(providerOptions));

        return value.Clone();
    }

    private static JsonObject CreateTokenLabPayload(JsonElement? providerOptions)
    {
        var payload = new JsonObject();
        if (providerOptions is not { ValueKind: JsonValueKind.Object } options)
            return payload;

        foreach (var property in options.EnumerateObject())
            payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        return payload;
    }

    private static void CopyAdditionalProperties(JsonObject payload, Dictionary<string, JsonElement>? additionalProperties)
    {
        if (additionalProperties is null)
            return;

        foreach (var property in additionalProperties)
            payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
    }

    private static StringContent ToJsonContent(JsonObject payload)
        => new(payload.ToJsonString(TokenLabJson), Encoding.UTF8, MediaTypeNames.Application.Json);

    private async Task<(JsonElement Root, IDictionary<string, string> Headers)> SendTokenLabJsonAsync(
        HttpMethod method,
        string endpoint,
        HttpContent? content,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TokenLab {operation} failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return (document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"TokenLab {operation} returned invalid JSON: {raw}", exception);
        }
    }

    private async Task<(byte[] Bytes, string MimeType, IDictionary<string, string> Headers)> SendTokenLabBinaryAsync(
        string endpoint,
        HttpContent content,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TokenLab {operation} failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Octet, response.GetHeaders());
    }

    private async Task<(JsonElement Root, IDictionary<string, string> Headers)> AwaitTokenLabTaskAsync(
        JsonElement create,
        IDictionary<string, string> createHeaders,
        string operation,
        CancellationToken cancellationToken)
    {
        var taskId = FindTokenLabString(create, "task_id", "taskId", "id");
        var pollUrl = FindTokenLabString(create, "poll_url", "pollUrl");
        if (string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(pollUrl))
            return (create, createHeaders);

        pollUrl ??= $"v1/tasks/{Uri.EscapeDataString(taskId!)}";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var poll = await SendTokenLabJsonAsync(HttpMethod.Get, pollUrl, null, $"{operation} poll", cancellationToken);
            var status = FindTokenLabString(poll.Root, "status", "state")?.ToLowerInvariant();

            if (status is "failed" or "failure" or "error" or "cancelled" or "canceled")
                throw new InvalidOperationException($"TokenLab {operation} task '{taskId}' failed: {FindTokenLabString(poll.Root, "error", "message", "detail") ?? poll.Root.GetRawText()}");

            if (status is "completed" or "complete" or "succeeded" or "success" or "done")
                return poll;

            if (string.IsNullOrWhiteSpace(status) && HasTokenLabMediaResult(poll.Root))
                return poll;
        }
    }

    private static string? FindTokenLabString(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
                if (element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    return value.ToString();

            foreach (var property in element.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var found = FindTokenLabString(property.Value, names);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
            {
                var found = FindTokenLabString(item, names);
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }

        return null;
    }

    private static bool HasTokenLabMediaResult(JsonElement root)
        => GetTokenLabMediaValues(root, "image").Count > 0
           || GetTokenLabMediaValues(root, "video").Count > 0;

    private static List<string> GetTokenLabMediaValues(JsonElement root, string mediaKind)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectTokenLabMediaValues(root, mediaKind, results, seen);
        return results;
    }

    private static void CollectTokenLabMediaValues(JsonElement element, string mediaKind, List<string> results, HashSet<string> seen)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectTokenLabMediaValues(item, mediaKind, results, seen);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && IsTokenLabMediaProperty(property.Name, mediaKind))
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                    results.Add(value);
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                CollectTokenLabMediaValues(property.Value, mediaKind, results, seen);
        }
    }

    private static bool IsTokenLabMediaProperty(string name, string mediaKind)
        => name.Equals("url", StringComparison.OrdinalIgnoreCase)
           || name.Equals("b64_json", StringComparison.OrdinalIgnoreCase)
           || name.Equals("base64", StringComparison.OrdinalIgnoreCase)
           || name.Equals(mediaKind, StringComparison.OrdinalIgnoreCase)
           || name.Equals($"{mediaKind}_url", StringComparison.OrdinalIgnoreCase)
           || name.Equals($"{mediaKind}Url", StringComparison.OrdinalIgnoreCase);

    private async Task<(byte[] Bytes, string MimeType)> ResolveTokenLabMediaAsync(
        string value,
        string fallbackMimeType,
        CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0)
                throw new InvalidOperationException("TokenLab returned an invalid data URL.");
            var mime = value[5..value.IndexOf(';')];
            return (Convert.FromBase64String(value[(comma + 1)..]), mime);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await _client.GetAsync(uri, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"TokenLab media download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
            return (bytes, response.Content.Headers.ContentType?.MediaType ?? fallbackMimeType);
        }

        return (Convert.FromBase64String(value), fallbackMimeType);
    }

    private Dictionary<string, JsonElement> CreateTokenLabMetadata(object value)
        => new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(value, TokenLabJson) };

    private static string GetFormatFromMimeType(string mimeType)
        => mimeType.Split('/').LastOrDefault()?.Split(';')[0] ?? "bin";
}
