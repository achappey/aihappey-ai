using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.AgDev;

public partial class AgDevProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private sealed record Transport(string Key, Dictionary<string, string>? Headers);
    private sealed record Reply(JsonElement Raw, Dictionary<string, object?> Headers);

    private Transport GetTransport(AIRequest? request = null)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        return new(string.IsNullOrWhiteSpace(key) ? throw new InvalidOperationException("No AgDev API key.") : key,
            request?.Headers);
    }

    private async Task<Reply> SendJson(Transport transport, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-API-Key", transport.Key);
        message.Headers.Accept.ParseAdd("application/json");
        foreach (var (name, value) in transport.Headers ?? [])
        {
            if (!ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader(name, "agdev")) continue;
            if (name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                throw new ArgumentException("AgDev headers cannot contain line breaks.");
            message.Headers.TryAddWithoutValidation(name, value);
        }
        if (body is not null)
            message.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Contains("token", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => (object?)h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (!response.IsSuccessStatusCode)
        {
            var error = new HttpRequestException($"AgDev API failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);
            error.Data["agdev.raw"] = raw;
            error.Data["agdev.headers"] = headers;
            error.Data["retryAfter"] = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
            throw error;
        }
        return new(JsonSerializer.Deserialize<JsonElement>(raw, Json).Clone(), headers);
    }

    private static string Enc(string value) => Uri.EscapeDataString(value);
    private static JsonElement? Property(JsonElement raw, string name)
        => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value) ? value : null;
    private static string? String(JsonElement raw, string name)
        => Property(raw, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static bool Flag(JsonElement raw, string name) => Property(raw, name) is { ValueKind: JsonValueKind.True };
    private static IEnumerable<JsonElement> Array(JsonElement raw, string name)
        => Property(raw, name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    private static string AgentId(string? model)
    {
        var id = model?.StartsWith("agdev/", StringComparison.OrdinalIgnoreCase) == true ? model[6..] : model;
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#'))
            throw new ArgumentException("AgDev requires model 'agdev/{agentId}'.");
        return id;
    }

    private static JsonObject Options(AIRequest request)
        => request.Metadata?.TryGetValue("agdev", out var value) == true && value is not null
            ? JsonSerializer.SerializeToNode(value, Json) as JsonObject
                ?? throw new ArgumentException("AgDev provider metadata must be a JSON object.")
            : new();

    private static JsonObject Parameters(AIRequest request)
    {
        var latest = request.Input?.Items?.LastOrDefault(i => string.Equals(i.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (latest?.Content?.Any(p => p is not AITextContentPart) == true)
            throw new NotSupportedException("AgDev requires JSON text input; encode file references or other inputs inside the JSON object.");
        var text = latest is null ? request.Input?.Text
            : string.Join("\n", (latest.Content ?? []).OfType<AITextContentPart>().Select(p => p.Text));
        try
        {
            // No prose conversion, code-fence stripping, schema inference, or automatic envelope.
            return JsonNode.Parse(text ?? "") as JsonObject ?? throw new JsonException("Expected a JSON object.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("AgDev requires the latest user input to be a strict JSON object (use {} for no inputs).", nameof(request), error);
        }
    }
}
