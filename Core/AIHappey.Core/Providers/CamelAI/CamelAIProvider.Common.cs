using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.CamelAI;

public sealed partial class CamelAIProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static JsonElement Serialize(object? value) => value is JsonElement element
        ? element.Clone() : JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) ? property.Clone() : null;
    private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static string? Text(JsonObject value, string name) => value[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
    private static string AgentId(string? model)
    {
        var local = model ?? "";
        if (local.StartsWith("camelai/", StringComparison.OrdinalIgnoreCase)) local = local[8..];
        var parts = local.Split('/');
        if (parts.Length != 2 || parts[0] != "agents" || string.IsNullOrWhiteSpace(parts[1]) || parts[1] is "." or "..")
            throw new ArgumentException("CamelAI models must be camelai/agents/{agentId}.");
        return parts[1];
    }
    private static string AgentPath(string id) => $"v1/agents/{Uri.EscapeDataString(id)}";

    // Endpoint mappers retain metadata differently. Read only the caller's provider namespace.
    private static JsonObject Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("camelai", out var direct) == true)
            return Object(direct, "CamelAI options");
        foreach (var key in new[] { "chatcompletions.request.metadata", "messages.request.metadata" })
            if (request.Metadata?.TryGetValue(key, out var raw) == true && Property(Serialize(raw), "camelai") is { } scoped)
                return Object(scoped, "CamelAI options");
        return [];
    }
    private static JsonObject Object(object? value, string name) => JsonNode.Parse(Serialize(value).GetRawText()) as JsonObject
        ?? throw new ArgumentException($"{name} must be a JSON object.");

    private async Task<JsonElement> SendJson(HttpMethod method, string path, JsonNode? body, CancellationToken ct,
        string? idempotencyKey = null, string? traceparent = null)
    {
        // Retry only reads or mutations with an explicit stable idempotency identity.
        for (var attempt = 0; ; attempt++)
        {
            using var http = CreateRequest(method, path);
            if (idempotencyKey is not null) http.Headers.Add("Idempotency-Key", idempotencyKey);
            if (traceparent is not null) http.Headers.Add("traceparent", traceparent);
            if (body is not null) http.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, MediaTypeNames.Application.Json);
            using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
            if (attempt < 2 && (method == HttpMethod.Get || idempotencyKey is not null || Text(body as JsonObject ?? [], "requestId") is not null)
                && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(attempt + 1);
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct);
                continue;
            }
            await EnsureSuccess(response, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.Clone();
        }
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        var exception = new HttpRequestException($"CamelAI HTTP {(int)response.StatusCode}: {raw}", null, response.StatusCode);
        exception.Data["camelai.raw"] = raw;
        if (response.Headers.RetryAfter is { } retry)
            exception.Data["Retry-After"] = retry.ToString();
        try
        {
            exception.Data["camelai.code"] = String(Serialize(JsonNode.Parse(raw)), "code") ?? "unknown";
        }
        catch (JsonException) { }
        throw exception;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var raw = await SendJson(HttpMethod.Get, "v1/agents", null, ct);
            if (raw.ValueKind != JsonValueKind.Array) throw new JsonException("CamelAI agent list must be an array.");
            return (IEnumerable<Model>)raw.EnumerateArray().Where(agent => String(agent, "id") is { Length: > 0 })
                .Select(agent => new Model
                {
                    Id = $"agents/{String(agent, "id")}".ToModelId(GetIdentifier()),
                    Name = String(agent, "name") ?? String(agent, "id")!,
                    Type = "language",
                    OwnedBy = "CamelAI",
                    Tags = ["agent"],
                    Description = $"CamelRun agent using {String(agent, "model")}. History and workspace belong to this agent."
                }).DistinctBy(model => model.Id).ToArray();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }

    private static Dictionary<string, object?> Metadata(string agent, JsonElement raw) => new()
    {
        ["camelai"] = new Dictionary<string, object?> { ["agentId"] = agent, ["requestId"] = String(raw, "id"), ["raw"] = raw.Clone() }
    };
    private static Dictionary<string, Dictionary<string, object>> Scoped(JsonElement raw) => new() { ["camelai"] = new() { ["raw"] = raw.Clone() } };
    private static AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?>? metadata = null) => new()
    {
        ProviderId = "camelai",
        Event = new AIEventEnvelope
        {
            Type = type,
            Id = id,
            Data = data,
            Metadata = metadata,
            Timestamp = DateTimeOffset.UtcNow
        },
        Metadata = metadata
    };
}
