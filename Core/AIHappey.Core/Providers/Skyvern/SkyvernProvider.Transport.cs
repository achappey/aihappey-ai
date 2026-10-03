using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Skyvern;

public partial class SkyvernProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private sealed record Transport(string Key, Dictionary<string, string>? Headers);
    private sealed record Reply(JsonElement Raw, Dictionary<string, object?> Headers);

    private Transport GetTransport(AIRequest? request = null)
        => new(_keyResolver.Resolve(GetIdentifier()) is { Length: > 0 } key && !string.IsNullOrWhiteSpace(key)
            ? key : throw new InvalidOperationException("No Skyvern API key."), request?.Headers);

    private HttpRequestMessage CreateRequest(Transport transport, HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Add("x-api-key", transport.Key);
        message.Headers.Accept.ParseAdd("application/json");
        foreach (var (name, value) in transport.Headers ?? [])
        {
            // Only forward the gateway's provider-header subset. API authentication and
            // framing are always controlled here, including when called outside a host.
            if (ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader(name, "skyvern"))
            {
                if (name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                    throw new ArgumentException("Skyvern headers cannot contain line breaks.");
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return message;
    }

    private async Task<Reply> SendJson(Transport transport, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var message = CreateRequest(transport, method, path);
        if (body is not null)
            message.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadReply(response, ct);
    }

    private static async Task<Reply> ReadReply(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        var headers = SafeResponseHeaders(response);
        if (!response.IsSuccessStatusCode)
        {
            var error = new HttpRequestException($"Skyvern API failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);
            error.Data["skyvern.headers"] = headers;
            error.Data["skyvern.raw"] = raw;
            var retry = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
            error.Data["retryAfter"] = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 30));
            throw error;
        }
        return new Reply(string.IsNullOrWhiteSpace(raw) ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.Deserialize<JsonElement>(raw, Json).Clone(), headers);
    }

    private static Dictionary<string, object?> SafeResponseHeaders(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Contains("token", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => (object?)header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private static string Enc(string value) => Uri.EscapeDataString(value);
    private static JsonElement? Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
    private static string? String(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static bool Flag(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.True };
    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    private static JsonObject Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("skyvern", out var value) != true || value is null) return new();
        return JsonNode.Parse(JsonSerializer.Serialize(value, Json)) as JsonObject
            ?? throw new ArgumentException("Skyvern provider metadata must be a JSON object.");
    }

    private static string AgentId(string? model)
    {
        var id = model?.StartsWith("skyvern/", StringComparison.OrdinalIgnoreCase) == true ? model[8..] : model;
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("wpid_", StringComparison.Ordinal)
            || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("Skyvern requires a saved-agent model 'skyvern/wpid_...'.");
        return id;
    }

    private static JsonElement Parameters(AIRequest request)
    {
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var text = latest is null ? request.Input?.Text
            : string.Join("\n", (latest.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text));
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(text ?? "", Json);
            if (parsed.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");
            return parsed.Clone();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Skyvern requires the latest user input to be a strict JSON object of workflow parameters (use {} for no parameters).", nameof(request), error);
        }
    }
}
