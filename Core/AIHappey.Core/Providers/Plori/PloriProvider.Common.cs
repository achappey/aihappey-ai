using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Plori;

public sealed partial class PloriProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private const string IdentityTool = "plori_agent_run";

    private async Task<JsonElement> RestAsync(HttpMethod method, string path, object? body, string? idempotencyKey,
        CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Plori {method} {path} failed ({(int)response.StatusCode}): {text}", null, response.StatusCode);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("https://api.plori.ai/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Key()}" }
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    // The public REST OpenAPI does not describe MCP arguments. Check the negotiated
    // tools/list schema rather than guessing whether this server wants 'agent' or 'agent_id'.
    private static Dictionary<string, object> McpArgs(JsonElement schema, params (string[] Names, object? Value)[] entries)
    {
        var result = new Dictionary<string, object>();
        var properties = Prop(schema, "properties");
        foreach (var (names, value) in entries)
        {
            if (value is null) continue;
            var name = names.FirstOrDefault(n => properties is { } p && Prop(p, n) is not null);
            if (name is not null) result[name] = value;
        }
        return result;
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string name, CancellationToken ct,
        params (string[] Names, object? Value)[] entries)
    {
        var tool = (await client.ListToolsAsync(cancellationToken: ct)).FirstOrDefault(t => t.Name == name)
            ?? throw new NotSupportedException($"Plori MCP tool '{name}' is unavailable.");
        var schema = tool.JsonSchema;
        var args = McpArgs(schema, entries);
        var result = await client.CallToolAsync(name, args, cancellationToken: ct);
        if (result.IsError == true)
            throw new InvalidOperationException($"Plori MCP {name} failed: {JsonSerializer.Serialize(result, Json)}");
        var raw = JsonSerializer.SerializeToElement(result, Json);
        if (Prop(raw, "structuredContent") is { ValueKind: JsonValueKind.Object } structured) return structured.Clone();
        if (Prop(raw, "content") is { ValueKind: JsonValueKind.Array } content)
            foreach (var part in content.EnumerateArray())
            {
                if (Str(part, "text") is not { } text) continue;
                try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
                catch (JsonException) { return JsonSerializer.SerializeToElement(new { reply = text }, Json); }
            }
        return raw;
    }

    private static JsonElement? Prop(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Str(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (Prop(value, name) is { ValueKind: JsonValueKind.String } p) return p.GetString();
        return null;
    }

    private static JsonElement Element(object? value) => value is JsonElement element
        ? element : JsonSerializer.SerializeToElement(value, Json);

    private static string? Find(AIRequest request, params string[] names)
    {
        if (request.Metadata is { } metadata && metadata.TryGetValue("plori", out var option))
        {
            var value = Element(option);
            if (Str(value, names) is { Length: > 0 } found) return found;
        }
        if (request.Input?.Metadata is { } input)
        {
            var value = Element(input);
            if (Str(value, names) is { Length: > 0 } found) return found;
            if (Prop(value, "plori") is { } nested && Str(nested, names) is { Length: > 0 } scoped) return scoped;
        }
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
        {
            if (item.Metadata is not null && DeepFind(Element(item.Metadata), names) is { } found) return found;
            foreach (var part in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
                if (part.ProviderExecuted == true && part.ToolName == IdentityTool)
                    foreach (var source in new[] { part.Output, part.Metadata, part.Input })
                        if (source is not null && DeepFind(Element(source), names) is { } identity) return identity;
        }
        return null;
    }

    private static string? DeepFind(JsonElement value, string[] names, int depth = 0)
    {
        if (depth > 5 || value.ValueKind != JsonValueKind.Object) return null;
        if (Str(value, names) is { Length: > 0 } direct) return direct;
        foreach (var key in new[] { "structuredContent", "output", "plori", "identity", "metadata", "run" })
            if (Prop(value, key) is { } nested && DeepFind(nested, names, depth + 1) is { } result) return result;
        return null;
    }

    private static string Agent(AIRequest request)
    {
        var model = request.Model ?? "";
        var id = model.StartsWith("plori/", StringComparison.OrdinalIgnoreCase) ? model[6..] : model;
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id is "agents" or "runs")
            throw new ArgumentException("Plori requires an existing agent ID as the model (plori/{agent_id}).");
        return id;
    }

    private static Dictionary<string, object?> Meta(string agent, JsonElement raw, string? session = null, string? run = null)
        => new()
        {
            ["plori"] = new { agent_id = agent, run_id = run ?? Str(raw, "run_id", "id"),
                session_id = session ?? Str(raw, "session_id", "thread_id"), raw = raw.Clone() }
        };
}
