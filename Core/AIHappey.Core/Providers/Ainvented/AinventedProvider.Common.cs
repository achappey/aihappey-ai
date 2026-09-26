using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Ainvented;

public sealed partial class AinventedProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    private static JsonElement AsJson(object? value)
        => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, Json);

    private static JsonObject Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("ainvented", out var value) != true || value is null)
            return [];
        var node = JsonNode.Parse(AsJson(value).GetRawText());
        return node as JsonObject ?? throw new ArgumentException("Ainvented provider options must be an object.");
    }

    private static string? String(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var property)
           && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string? Option(JsonObject options, string key)
        => options[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonElement? Property(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value.Clone() : null;

    private static Dictionary<string, object?> Metadata(JsonElement raw, string? requestId = null)
    {
        var metadata = new Dictionary<string, object?> { ["ainvented.raw"] = raw.Clone() };
        foreach (var key in new[] { "session_id", "task_results", "context_truncation", "response", "image_meta", "id", "source_session_id", "status" })
            if (Property(raw, key) is { } value)
                metadata[$"ainvented.{key}"] = value;
        if (!string.IsNullOrWhiteSpace(requestId)) metadata["ainvented.request_id"] = requestId;
        return metadata;
    }

    private static string? RequestId(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-Request-Id", out var values) ? values.FirstOrDefault() : null;

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, JsonObject? payload, CancellationToken ct)
    {
        using var req = CreateRequest(method, path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) req.Content = new StringContent(payload.ToJsonString(Json), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Ainvented HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);
    }

    private static string Route(string? model)
    {
        var local = model?.Trim() ?? "";
        if (local.StartsWith("ainvented/", StringComparison.OrdinalIgnoreCase)) local = local[10..];
        if (local == "workflow" || local.StartsWith("task/", StringComparison.Ordinal)) return local;
        // Ainvented silently accepts unknown tier names as default; the gateway must not
        // accidentally send a synthetic model id to chat, which would hide a routing error.
        if (local.Length == 0) return "ainvented-default";
        if (new[] { "ainvented-default", "ainvented-low", "ainvented-saga", "ainvented-epic", "ainvented-lite", "default", "low", "saga", "epic", "lite" }
            .Contains(local, StringComparer.OrdinalIgnoreCase)) return local;
        throw new ArgumentException($"Unknown Ainvented model '{model}'.", nameof(model));
    }

    private static AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = "ainvented",
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data },
            Metadata = metadata
        };
}
