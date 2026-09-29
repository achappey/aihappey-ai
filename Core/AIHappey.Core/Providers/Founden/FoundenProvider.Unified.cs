using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Founden;

public sealed partial class FoundenProvider
{
    private const string IdentityTool = "founden_build_execution";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Model, "founden/builds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected Founden model 'founden/builds'.", nameof(request));
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("Founden builds do not accept gateway tool definitions.");

        var options = GetOptions(request);
        var id = GetString(options, "execution_id") ?? FindExecutionId(request);
        var action = GetString(options, "action") ?? (id is null ? "start" : "message");
        if (action is not ("start" or "message" or "status" or "stop"))
            throw new ArgumentException("Founden action must be start, message, status or stop.", nameof(request));
        if (action != "start" && string.IsNullOrWhiteSpace(id))
            throw new ArgumentException($"Founden {action} requires an execution_id in provider metadata or a prior Founden execution tool call.", nameof(request));
        if (action == "start" && !string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Founden start cannot target an existing execution; omit execution_id or use action 'message'.", nameof(request));

        var prompt = action is "start" or "message" ? LatestUserText(request) : null;
        if (action is "start" or "message" && string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException($"Founden {action} requires a nonempty user prompt.", nameof(request));
        if (action != "start" && GetString(options, "company_id") is not null)
            throw new ArgumentException("Founden company_id is only valid when starting a build.", nameof(request));

        var path = action == "start" ? "v1/builds" : $"v1/builds/{Uri.EscapeDataString(id!)}";
        if (action == "message" || action == "stop") path += "/" + action;
        object? body = action switch
        {
            "start" when GetString(options, "company_id") is { } companyId => new { prompt, company_id = companyId },
            "start" or "message" => new { prompt },
            _ => null
        };
        var (raw, headers) = await SendAsync(action == "status" ? HttpMethod.Get : HttpMethod.Post, path, body, cancellationToken);
        var data = Property(raw, "data");
        var executionId = GetString(data, "execution_id") ?? id;
        if (action == "start" && string.IsNullOrWhiteSpace(executionId))
            throw new InvalidOperationException("Founden build start did not return an execution_id.");

        var metadata = new Dictionary<string, object?>
        {
            ["founden.raw"] = raw.Clone(),
            ["founden.action"] = action,
            ["founden.headers"] = headers
        };
        if (data is { } result) metadata["founden.data"] = result.Clone();
        if (Property(raw, "metadata") is { } providerMetadata) metadata["founden.metadata"] = providerMetadata.Clone();
        if (Property(raw, "credits_used") is { } credits) metadata["founden.credits_used"] = credits.Clone();
        if (executionId is not null) metadata["founden.execution_id"] = executionId;
        foreach (var name in new[] { "company_id", "status", "stream_url", "preview_url" })
            if (Property(data, name) is { } value) metadata[$"founden.{name}"] = value.Clone();

        var parts = new List<AIContentPart>();
        if (executionId is not null)
            parts.Add(new AIToolCallContentPart
            {
                Type = "tool-call", ToolName = IdentityTool, Title = "Founden build execution",
                ToolCallId = $"founden-execution-{executionId}", ProviderExecuted = true, State = "output-available",
                Input = new { action, execution_id = executionId },
                Output = new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    execution_id = executionId, raw = raw.Clone()
                }, Json) }, Metadata = metadata
            });
        if (GetString(data, "message") is { Length: > 0 } text)
            parts.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
        if (GetString(data, "preview_url") is { Length: > 0 } previewUrl)
            parts.Add(new AITextContentPart { Type = "text", Text = previewUrl, Metadata = metadata });

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = "founden/builds", Status = GetString(data, "status") ?? "completed",
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = parts, Metadata = metadata }], Metadata = metadata },
            Metadata = metadata
        };
    }

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Founden live streaming is not supported until the stream_url protocol is documented. Use a non-streaming request and poll with founden.action=status.");

    private static JsonElement GetOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("founden", out var value) != true || value is null) return default;
        var options = Serialize(value);
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Founden provider metadata must be an object.", nameof(request));
        foreach (var property in options.EnumerateObject())
        {
            if (property.Name is not ("action" or "execution_id" or "company_id"))
                throw new ArgumentException($"Unknown Founden option '{property.Name}'.", nameof(request));
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw new ArgumentException($"Founden option '{property.Name}' must be a nonempty string.", nameof(request));
        }
        return options;
    }

    private static string? LatestUserText(AIRequest request)
    {
        var item = request.Input?.Items?.LastOrDefault(i => string.Equals(i.Role, "user", StringComparison.OrdinalIgnoreCase));
        return item is null ? request.Input?.Text : string.Join("\n", item.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
    }

    private static string? FindExecutionId(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
            foreach (var part in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
            {
                if (part.ProviderExecuted != true || part.ToolName != IdentityTool || part.Output is null) continue;
                var output = Serialize(part.Output);
                var content = Property(output, "structuredContent") ?? output;
                if (GetString(content, "execution_id") is { Length: > 0 } id) return id;
            }
        return null;
    }

    private static JsonElement Serialize(object value)
        => value is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(value, Json);

    private static JsonElement? Property(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property)
            ? property.Clone() : null;

    private static string? GetString(JsonElement? element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private async Task<(JsonElement Body, Dictionary<string, object?> Headers)> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Founden (Suprsonic) API key.");
        using var http = new HttpRequestMessage(method, path);
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (body is not null)
            http.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement raw;
        try { raw = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException ex) { throw new HttpRequestException($"Founden returned invalid JSON (HTTP {(int)response.StatusCode}).", ex, response.StatusCode); }

        if (!response.IsSuccessStatusCode || raw.ValueKind != JsonValueKind.Object ||
            Property(raw, "success") is not { ValueKind: JsonValueKind.True })
        {
            var error = Property(raw, "error");
            var detail = GetString(error, "detail") ?? GetString(error, "title") ?? "Upstream request failed";
            var requestId = GetString(Property(raw, "metadata"), "request_id");
            throw new HttpRequestException($"Founden HTTP {(int)response.StatusCode}: {detail}"
                + (requestId is null ? "" : $" (request_id: {requestId})"), null, response.StatusCode);
        }
        var headers = new Dictionary<string, object?>();
        foreach (var name in new[] { "X-Credits-Used", "X-RateLimit-Remaining", "X-RateLimit-Limit", "X-RateLimit-Reset", "Retry-After" })
            if (response.Headers.TryGetValues(name, out var values)) headers[name] = string.Join(",", values);
        return (raw, headers);
    }
}
