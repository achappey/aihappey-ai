using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AgentDiscuss;

public partial class AgentDiscussProvider
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (domain, capability) = ParseRoute(request.Model);
        var payload = BuildPayload(request);
        var raw = await ExecuteCapabilityAsync(domain, capability, payload, cancellationToken);
        return MapResponse(request.Model!, domain, capability, payload, raw);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (domain, capability) = ParseRoute(request.Model);
        var payload = BuildPayload(request);
        var callId = request.Id ?? $"agentdiscuss-{Guid.NewGuid():N}";
        var toolName = ToolName(domain, capability);
        var now = DateTimeOffset.UtcNow;
        yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
        {
            ToolName = toolName,
            Title = $"AgentDiscuss {domain}.{capability}",
            Input = payload.Clone(),
            ProviderExecuted = true
        }, now);

        var raw = await ExecuteCapabilityAsync(domain, capability, payload, cancellationToken);
        var response = MapResponse(request.Model!, domain, capability, payload, raw);
        var metadata = response.Metadata;
        yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
        {
            ToolName = toolName,
            Output = CreateToolResult(raw),
            ProviderExecuted = true,
            Dynamic = true,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = new Dictionary<string, object> { ["raw"] = raw.Clone() }
            }
        }, DateTimeOffset.UtcNow, metadata);

        // Execute is synchronous at the AgentDiscuss API: this stream is a unified, synthetic lifecycle.
        var finished = DateTimeOffset.UtcNow;
        yield return Event("finish", callId, new AIFinishEventData
        {
            FinishReason = "stop",
            Model = request.Model,
            CompletedAt = finished.ToUnixTimeSeconds(),
            Response = raw.Clone(),
            MessageMetadata = AIFinishMessageMetadata.Create(request.Model!, finished,
                additionalProperties: new Dictionary<string, object?> { ["agentdiscuss"] = raw.Clone() })
        }, finished, metadata, response.Output);
    }

    private async Task<JsonElement> ExecuteCapabilityAsync(string domain, string capability, JsonElement payload,
        CancellationToken cancellationToken)
    {
        var path = $"api/agentic-api/domains/{Uri.EscapeDataString(domain)}/capabilities/{Uri.EscapeDataString(capability)}/execute";
        var raw = await SendAgentDiscussAsync(HttpMethod.Post, path, payload, cancellationToken);
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"AgentDiscuss execution failed: {DescribeError(raw)}");
        return raw;
    }

    private async Task<JsonElement> SendAgentDiscussAsync(HttpMethod method, string path, JsonElement? payload,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path);
        Authenticate(message);
        if (payload.HasValue)
            message.Content = new StringContent(payload.Value.GetRawText(), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail;
            try { using var document = JsonDocument.Parse(body); detail = DescribeError(document.RootElement); }
            catch (JsonException) { detail = body; }
            throw new HttpRequestException($"AgentDiscuss {method} {path} failed ({(int)response.StatusCode}): {detail}",
                null, response.StatusCode);
        }

        using var result = JsonDocument.Parse(body);
        return result.RootElement.Clone();
    }

    private static string DescribeError(JsonElement raw)
    {
        var error = GetString(raw, "error") ?? raw.ToString();
        var code = GetString(raw, "code");
        var hint = GetString(raw, "hint");
        return string.Join("; ", new[] { code, error, hint }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static (string Domain, string Capability) ParseRoute(string? model)
    {
        var segments = model?.Split('/');
        if (segments is not { Length: 3 } || !string.Equals(segments[0], "agentdiscuss", StringComparison.OrdinalIgnoreCase)
            || !IsValidSegment(segments[1]) || !IsValidSegment(segments[2]))
            throw new ArgumentException("Expected AgentDiscuss model 'agentdiscuss/{domainKey}/{capabilityId}'.", nameof(model));
        return (segments[1], segments[2]);
    }

    // Restrict each model component to a single literal URL segment (no dots, encoded slashes or traversal).
    private static bool IsValidSegment(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private JsonElement BuildPayload(AIRequest request)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var options) == true && options is not null)
        {
            var metadata = options is JsonElement json ? json : JsonSerializer.SerializeToElement(options, JsonOptions);
            if (metadata.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("AgentDiscuss provider metadata must be a JSON object.", nameof(request));
            foreach (var property in metadata.EnumerateObject())
                merged[property.Name] = property.Value.Clone();
        }

        var text = request.Input?.Items?
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>()
                .Select(part => part.Text).Where(part => !string.IsNullOrWhiteSpace(part)) ?? []))
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
        text ??= request.Input?.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            JsonElement input;
            try { using var document = JsonDocument.Parse(text); input = document.RootElement.Clone(); }
            catch (JsonException exception)
            {
                throw new ArgumentException("AgentDiscuss user text must contain a JSON object.", nameof(request), exception);
            }

            if (input.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("AgentDiscuss user text must contain a JSON object.", nameof(request));
            foreach (var property in input.EnumerateObject())
                merged[property.Name] = property.Value.Clone();
        }

        if (merged.Count == 0)
            throw new ArgumentException("AgentDiscuss requires JSON user text or provider-scoped JSON metadata.", nameof(request));
        return JsonSerializer.SerializeToElement(merged, JsonOptions);
    }

    private AIResponse MapResponse(string model, string domain, string capability, JsonElement input, JsonElement raw)
    {
        var metadata = CreateMetadata(raw, domain, capability);
        var part = new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = GetString(raw, "providerMessageId") ?? $"agentdiscuss-{Guid.NewGuid():N}",
            ToolName = ToolName(domain, capability),
            Title = $"AgentDiscuss {domain}.{capability}",
            Input = input.Clone(),
            Output = CreateToolResult(raw),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = metadata
        };
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = GetString(raw, "status") ?? "completed",
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Content = [part], Metadata = metadata }],
                Metadata = metadata
            },
            Metadata = metadata
        };
    }

    private static CallToolResult CreateToolResult(JsonElement raw) => new()
    {
        Content = [],
        StructuredContent = raw.Clone()
    };

    private static string ToolName(string domain, string capability) => $"agentdiscuss_{domain}_{capability}";

    private static Dictionary<string, object?> CreateMetadata(JsonElement raw, string domain, string capability) => new()
    {
        ["agentdiscuss.raw"] = raw.Clone(),
        ["agentdiscuss.domainKey"] = domain,
        ["agentdiscuss.capabilityId"] = capability,
        ["agentdiscuss.provider"] = GetString(raw, "provider"),
        ["agentdiscuss.routeKey"] = GetString(raw, "routeKey"),
        ["agentdiscuss.providerMessageId"] = GetString(raw, "providerMessageId"),
        ["agentdiscuss.creditsCharged"] = raw.TryGetProperty("creditsCharged", out var credits) ? credits.Clone() : null
    };

    private AIStreamEvent Event(string type, string id, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null, AIOutput? output = null) => new()
    {
        ProviderId = GetIdentifier(),
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = timestamp, Output = output },
        Metadata = metadata
    };
}
