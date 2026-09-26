using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Runtype;

public sealed partial class RuntypeProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private const string IdentityTool = "runtype_execution";

    private enum RouteKind { List, Execute, Detail, Events, Status }
    private sealed record Route(RouteKind Kind, string? AgentId, string Model);
    private sealed record JsonReply(JsonElement Body, int HttpStatus, Dictionary<string, object?> Headers);

    private static Route ParseRoute(string? model)
    {
        var local = model?.Trim().Trim('/') ?? "";
        if (local.StartsWith("runtype/", StringComparison.OrdinalIgnoreCase)) local = local[8..];
        var parts = local.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0] == "agents") return new(RouteKind.List, null, "runtype/agents");
        if (parts.Length == 2 && parts[0] == "executions" && parts[1] == "status")
            return new(RouteKind.Status, null, "runtype/executions/status");
        if (parts.Length >= 2 && parts[0] == "agents" && !string.IsNullOrWhiteSpace(parts[1])
            && parts[1] is not ("execution" or "events"))
        {
            if (parts.Length == 2) return new(RouteKind.Execute, parts[1], $"runtype/agents/{parts[1]}");
            if (parts.Length == 3 && parts[2] == "execution")
                return new(RouteKind.Detail, parts[1], $"runtype/agents/{parts[1]}/execution");
            if (parts.Length == 3 && parts[2] == "events")
                return new(RouteKind.Events, parts[1], $"runtype/agents/{parts[1]}/events");
        }
        throw new ArgumentException($"Unknown Runtype model '{model}'.", nameof(model));
    }

    private static JsonObject Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("runtype", out var raw) != true || raw is null) return [];
        var node = JsonNode.Parse(Serialize(raw).GetRawText());
        return node as JsonObject ?? throw new ArgumentException("Runtype provider options must be an object.");
    }

    private static JsonElement Serialize(object? value)
        => value is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(value, Json);

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static JsonElement? Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property.Clone() : null;

    private static string? Text(JsonObject options, string name)
        => options[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Boolean(JsonObject options, string name)
        => options[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string RequiredId(JsonObject options, AIRequest request, string name)
    {
        var id = Text(options, name) ?? FindIdentity(request, name);
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException($"Runtype {name} is required in provider options or a previous Runtype execution identity.");
        return id;
    }

    private static string? FindIdentity(AIRequest request, string name)
    {
        // Identity is a provider-executed synthetic tool, never an arbitrary tool result.
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            foreach (var part in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
            {
                if (part.ProviderExecuted != true || part.ToolName != IdentityTool || part.Output is null) continue;
                var result = Serialize(part.Output);
                var content = Property(result, "structuredContent") ?? result;
                if (String(content, name) is { Length: > 0 } id) return id;
            }
        }
        return null;
    }

    private static JsonObject BuildExecuteBody(AIRequest request, JsonObject options, bool stream)
    {
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("Gateway client tool definitions cannot run on Runtype. Use runtype.tools.runtimeTools for server-executed tools.");
        var body = new JsonObject();
        if (options["body"] is JsonObject extra)
        {
            foreach (var pair in extra)
            {
                if (pair.Key is "messages" or "history" or "conversationId" or "streamResponse"
                    or "clientTools" or "resumeFrom" or "secrets" or "toolOutputs")
                    throw new ArgumentException($"Runtype body override cannot set '{pair.Key}'.");
                body[pair.Key] = pair.Value?.DeepClone();
            }
        }
        foreach (var key in new[] { "durability", "tools", "options" })
            if (options.TryGetPropertyValue(key, out var value)) body[key] = value?.DeepClone();
        if (body["clientTools"] is not null)
            throw new NotSupportedException("Runtype clientTools require resume, which is not supported by this provider.");

        var history = Text(options, "history") ?? "inline";
        if (history is not ("inline" or "stored")) throw new ArgumentException("Runtype history must be inline or stored.");
        var conversationId = Text(options, "conversationId") ?? FindIdentity(request, "conversationId");
        if (history == "stored" && string.IsNullOrWhiteSpace(conversationId))
            throw new ArgumentException("Stored Runtype history requires a conversationId.");
        if (!string.IsNullOrWhiteSpace(conversationId)) body["conversationId"] = conversationId;
        body["history"] = history;
        body["messages"] = BuildMessages(request, history == "stored");
        var runtimeOptions = body["options"] as JsonObject ?? new JsonObject();
        runtimeOptions["streamResponse"] = stream;
        if (body["options"] is null) body["options"] = runtimeOptions;
        return body;
    }

    private static JsonArray BuildMessages(AIRequest request, bool stored)
    {
        var messages = new JsonArray();
        if (!stored && !string.IsNullOrWhiteSpace(request.Instructions))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.Instructions });

        var input = request.Input?.Items ?? [];
        var selected = stored
            ? input.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)) is { } last
                ? new List<AIInputItem> { last } : []
            : input;
        foreach (var item in selected)
        {
            var role = item.Role?.ToLowerInvariant();
            if (role is not ("user" or "assistant" or "system" or "tool")) continue;
            var content = item.Content ?? [];
            var text = string.Join("\n", content.OfType<AITextContentPart>().Select(part => part.Text));
            var parts = content.Where(part => part is not AIToolCallContentPart and not AIReasoningContentPart).ToList();
            var message = new JsonObject { ["role"] = role, ["content"] = ToRuntypeContent(parts, text) };
            var id = item.Id ?? (item.Metadata?.TryGetValue("runtype.id", out var rawId) == true ? rawId?.ToString() : null);
            if (!string.IsNullOrEmpty(id)) message["id"] = id;

            var calls = content.OfType<AIToolCallContentPart>().Where(part => part.ProviderExecuted != true).ToList();
            if (role == "assistant" && calls.Count > 0)
                message["toolCalls"] = new JsonArray(calls.Select(call => (JsonNode?)new JsonObject
                {
                    ["toolCallId"] = call.ToolCallId, ["toolName"] = call.ToolName,
                    ["args"] = call.Input is null ? new JsonObject() : JsonSerializer.SerializeToNode(call.Input, Json)
                }).ToArray());
            if (role == "tool")
            {
                if (calls.Count == 0) throw new ArgumentException("Runtype tool messages require a tool result.");
                message["toolResults"] = new JsonArray(calls.Select(call => (JsonNode?)new JsonObject
                {
                    ["toolCallId"] = call.ToolCallId, ["toolName"] = call.ToolName,
                    ["result"] = JsonSerializer.SerializeToNode(call.Output ?? text, Json)
                }).ToArray());
            }
            if (role == "tool" || calls.Count > 0 || parts.Count > 0 || !string.IsNullOrWhiteSpace(text)) messages.Add(message);
        }
        if (messages.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Input.Text });
        if (messages.Count == 0 || (stored && (messages.Count != 1 || (string?)((JsonObject)messages[0]!)["role"] != "user")))
            throw new ArgumentException("Runtype requires a user message; stored history sends only the latest user delta.");
        return messages;
    }

    private static JsonNode ToRuntypeContent(List<AIContentPart> parts, string text)
    {
        if (parts.All(part => part is AITextContentPart)) return JsonValue.Create(text)!;
        var result = new JsonArray();
        foreach (var part in parts)
        {
            switch (part)
            {
                case AITextContentPart value:
                    result.Add(new JsonObject { ["type"] = "text", ["text"] = value.Text });
                    break;
                case AIFileContentPart file:
                    var mime = file.MediaType ?? MediaTypeNames.Application.Octet;
                    var data = file.Data switch
                    {
                        byte[] bytes => Convert.ToBase64String(bytes),
                        BinaryData binary => Convert.ToBase64String(binary.ToArray()),
                        string encoded => encoded.Contains(";base64,", StringComparison.OrdinalIgnoreCase)
                            ? encoded[(encoded.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) + 8)..] : encoded,
                        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                        _ => throw new NotSupportedException("Runtype files require inline base64 data.")
                    };
                    if (data is null || !Convert.TryFromBase64String(data, new byte[data.Length], out _))
                        throw new ArgumentException("Runtype cannot fetch file URLs; provide inline base64 content.");
                    result.Add(new JsonObject { ["type"] = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "file",
                        ["data"] = data, ["mimeType"] = mime, ["filename"] = file.Filename });
                    break;
                default: throw new NotSupportedException($"Runtype does not support input part type '{part.Type}'.");
            }
        }
        return result;
    }

    private void ApplyExecuteHeaders(HttpRequestMessage request, AIRequest input, JsonObject options)
    {
        if (Boolean(options, "respondAsync")) request.Headers.TryAddWithoutValidation("Prefer", "respond-async");
        foreach (var (name, key) in new[] { ("Idempotency-Key", "idempotencyKey"),
                 ("x-runtype-concurrency", "concurrency"), ("x-runtype-coalesce", "coalesce") })
        {
            var value = Text(options, key);
            if (name == "Idempotency-Key" && string.IsNullOrWhiteSpace(value) && Boolean(options, "respondAsync"))
                value = input.Id;
            if (!string.IsNullOrWhiteSpace(value)) request.Headers.TryAddWithoutValidation(name, value);
        }
        // Only these Runtype protocol headers can be forwarded from generic request headers.
        foreach (var name in new[] { "Prefer", "Idempotency-Key", "x-runtype-concurrency", "x-runtype-coalesce" })
            if (input.Headers?.TryGetValue(name, out var value) == true)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var retry = response.Headers.RetryAfter?.ToString();
        throw new HttpRequestException($"Runtype HTTP {(int)response.StatusCode}: {body}"
            + (retry is null ? "" : $" (Retry-After: {retry})"), null, response.StatusCode);
    }

    private async Task<JsonReply> SendJson(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return new JsonReply(JsonDocument.Parse(raw).RootElement.Clone(), (int)response.StatusCode, Headers(response));
    }

    private static Dictionary<string, object?> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, object?>();
        foreach (var name in new[] { "Preference-Applied", "X-Runtype-Execution-Driver", "Retry-After" })
            if (response.Headers.TryGetValues(name, out var values)) headers[name] = string.Join(",", values);
        return headers;
    }

    private static Dictionary<string, object?> Metadata(JsonElement raw, string? sseId = null,
        Dictionary<string, object?>? headers = null)
    {
        var metadata = new Dictionary<string, object?> { ["runtype.raw"] = raw.Clone() };
        foreach (var key in new[] { "executionId", "agentExecutionId", "conversationId", "status", "statusUrl", "eventsUrl", "deliveryId", "deliveryStatusUrl", "pausedReason", "stopReason" })
            if (Property(raw, key) is { } value) metadata[$"runtype.{key}"] = value;
        if (sseId is not null) metadata["runtype.sseId"] = sseId;
        if (headers is { Count: > 0 }) metadata["runtype.headers"] = headers;
        return metadata;
    }

    private static Dictionary<string, Dictionary<string, object>> Scoped(JsonElement raw, string? cursor = null)
    {
        var details = new Dictionary<string, object> { ["raw"] = raw.Clone() };
        if (cursor is not null) details["sseId"] = cursor;
        return new() { ["runtype"] = details };
    }

    private AIStreamEvent Event(string kind, string? id, object data, Dictionary<string, object?>? metadata = null)
        => new() { ProviderId = GetIdentifier(), Event = new AIEventEnvelope
        { Type = kind, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata }, Metadata = metadata };

    private static AIToolCallContentPart IdentityPart(string agentId, JsonElement raw)
    {
        var executionId = String(raw, "executionId") ?? String(raw, "agentExecutionId") ?? String(raw, "id");
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = $"runtype-execution-{executionId ?? Guid.NewGuid().ToString("N")}",
            ToolName = IdentityTool, Title = "Runtype execution", ProviderExecuted = true,
            State = "output-available", Input = new { agentId },
            Output = new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(new
            {
                agentId, executionId, conversationId = String(raw, "conversationId"),
                status = String(raw, "status"), raw
            }, Json) }, Metadata = Metadata(raw)
        };
    }
}
