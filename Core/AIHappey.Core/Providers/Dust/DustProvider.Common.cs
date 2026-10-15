using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    private const string IdentityTool = "create_dust_conversation";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private sealed record Route(string WorkspaceId, string AgentId)
    {
        public string Model => $"dust/{WorkspaceId}/{AgentId}";
        public string Path => $"w/{Segment(WorkspaceId)}";
    }

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Dust identifiers must contain only ASCII letters, digits, underscores, or hyphens.");
        return Uri.EscapeDataString(value);
    }

    private static Route ParseRoute(string? model)
    {
        var parts = model?.Split('/');
        if (parts is { Length: 3 } && string.Equals(parts[0], "dust", StringComparison.OrdinalIgnoreCase))
            parts = parts[1..];
        if (parts is not { Length: 2 })
            throw new ArgumentException("Dust requires a model 'dust/{workspaceId}/{agentId}'.", nameof(model));
        Segment(parts[0]);
        Segment(parts[1]);
        return new Route(parts[0], parts[1]);
    }

    private static JsonNode? Node(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, Json)
    };

    private static string? Str(JsonNode? node, string name)
        => node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? OptionString(JsonNode? node, string camel, string snake)
        => Str(node, camel) ?? Str(node, snake);

    private static JsonObject Options(AIRequest request)
    {
        foreach (var key in new[] { "dust", "chatcompletions.request.dust", "messages.request.dust" })
            if (request.Metadata?.TryGetValue(key, out var value) == true && Node(value) is JsonObject options)
                return options;
        foreach (var key in new[] { "chatcompletions.request.metadata", "messages.request.metadata" })
            if (request.Metadata?.TryGetValue(key, out var value) == true && Node(value)?["dust"] is JsonObject options)
                return (JsonObject)options.DeepClone();
        return new();
    }

    private static AIRequest PrepareRequest(AIRequest request, Dictionary<string, JsonElement>? additional)
    {
        if (additional?.TryGetValue("dust", out var options) != true) return request;
        var metadata = request.Metadata is null ? new Dictionary<string, object?>() : new(request.Metadata);
        metadata["dust"] = options.Clone();
        return new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id, Instructions = request.Instructions,
            Input = request.Input, Temperature = request.Temperature, TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls, Stream = request.Stream, ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice, ResponseFormat = request.ResponseFormat, Tools = request.Tools,
            Headers = request.Headers, Verbosity = request.Verbosity, Metadata = metadata
        };
    }

    private static Dictionary<string, object?> Metadata(JsonNode raw)
        => new() { ["dust"] = JsonSerializer.SerializeToElement(raw, Json) };

    private static Dictionary<string, Dictionary<string, object>> ScopedMetadata(JsonNode raw)
        => new() { ["dust"] = new() { ["raw"] = JsonSerializer.SerializeToElement(raw, Json) } };

    private static JsonNode? DustMetadata(JsonNode? node)
    {
        if (node is not JsonObject metadata) return node;
        var dust = metadata["dust"] as JsonObject;
        foreach (var key in new[] { "messages.provider.call.metadata", "messages.provider.metadata", "messages.provider.result.metadata" })
            dust ??= (metadata[key] as JsonObject)?["dust"] as JsonObject;
        if (dust is null) return node;
        return OptionString(dust, "conversationId", "conversation_id") is null && dust["raw"] is JsonObject raw
            ? raw : dust;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? payload = null, bool sse = false)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Dust API key.");
        var request = new HttpRequestMessage(method, new Uri("https://dust.tt/api/" + (sse ? "sse/v1/" : "v1/") + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: Json);
        return request;
    }

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = CreateRequest(method, path, payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(body, cancellationToken: ct)
            ?? throw new InvalidOperationException("Dust returned an empty JSON response.");
    }

    private async IAsyncEnumerable<JsonNode> ReadEventsAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? eventType = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    if (data.ToString() == "done")
                    {
                        yield return new JsonObject { ["type"] = "dust_connection_done" };
                        yield break;
                    }
                    var parsed = JsonNode.Parse(data.ToString()) ?? throw new InvalidOperationException("Empty Dust SSE event.");
                    if (parsed is JsonObject obj && obj["type"] is null && eventType is not null)
                        obj["type"] = eventType;
                    yield return parsed;
                }
                data.Clear();
                eventType = null;
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].TrimStart(' ');
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart(' '));
            }
        }
        // An unterminated SSE frame is not proof that the upstream operation finished.
        if (data.Length > 0)
            throw new IOException("Dust SSE stream ended inside an event.");
    }

    private AIStreamEvent Event(string type, string? id, object data, JsonNode metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = Metadata(metadata),
            Event = new AIEventEnvelope
            {
                Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = Metadata(metadata)
            }
        };
}
