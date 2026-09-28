using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.ResearchAgent;

public partial class ResearchAgentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    // The chat mapper does not expose headers in its unified request; Messages has an
    // additional headers argument. Keep these on the request, never in body metadata.
    private static AIRequest WithHeaders(AIRequest request, IDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return request;
        var merged = new Dictionary<string, string>(request.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers) merged[name] = value;
        return new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id,
            Instructions = request.Instructions, Input = request.Input, Temperature = request.Temperature,
            TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls, Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls, ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat, Tools = request.Tools, Metadata = request.Metadata,
            Headers = merged, Verbosity = request.Verbosity
        };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (route, path) = ParseRoute(request.Model);
        var payload = BuildPayload(request, route);
        var raw = await SendAsync(HttpMethod.Post, path, payload, GetByok(request), cancellationToken);
        return MapResponse(request.Model!, route, payload, raw);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (route, path) = ParseRoute(request.Model);
        var payload = BuildPayload(request, route);
        var callId = request.Id ?? $"researchagent-{Guid.NewGuid():N}";
        var toolName = ToolName(route);
        yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
        {
            ToolName = toolName, Title = $"ResearchAgent {route}", Input = payload.Clone(), ProviderExecuted = true
        }, DateTimeOffset.UtcNow);

        // The upstream execution is synchronous; this is a synthetic unified lifecycle.
        var raw = await SendAsync(HttpMethod.Post, path, payload, GetByok(request), cancellationToken);
        var mapped = MapResponse(request.Model!, route, payload, raw);
        yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
        {
            ToolName = toolName, Output = CreateToolResult(raw), ProviderExecuted = true, Dynamic = true,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = new() { ["raw"] = raw.Clone() }
            }
        }, DateTimeOffset.UtcNow, mapped.Metadata);

        var finished = DateTimeOffset.UtcNow;
        yield return Event("finish", callId, new AIFinishEventData
        {
            FinishReason = "stop", Model = request.Model, CompletedAt = finished.ToUnixTimeSeconds(),
            Response = raw.Clone(),
            MessageMetadata = AIFinishMessageMetadata.Create(request.Model!, finished,
                additionalProperties: new Dictionary<string, object?> { [GetIdentifier()] = raw.Clone() })
        }, finished, mapped.Metadata, mapped.Output);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonElement? payload,
        string? byok, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path);
        Authenticate(message);
        if (!string.IsNullOrWhiteSpace(byok)) message.Headers.Add("X-OpenRouter-API-Key", byok);
        if (payload.HasValue)
            message.Content = new StringContent(payload.Value.GetRawText(), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            // Do not echo arbitrary upstream error bodies: they may include BYOK credentials.
            throw new HttpRequestException($"ResearchAgent {method} {path} failed ({(int)response.StatusCode}).",
                null, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string? GetByok(AIRequest request)
        => request.Headers?.FirstOrDefault(header => string.Equals(header.Key, "X-OpenRouter-API-Key",
            StringComparison.OrdinalIgnoreCase)).Value;

    private static (string Route, string Path) ParseRoute(string? model)
    {
        var segments = model?.Split('/');
        if (segments is null || segments.Length < 2 || segments[0] != "researchagent")
            throw new ArgumentException("Expected ResearchAgent model 'researchagent/research' or 'researchagent/agents/{id}'.", nameof(model));
        if (segments is [_, "research"]) return ("research", "research");
        if (segments is [_, "agents", var id] && IsValidId(id))
            return ($"agents/{id}", $"agents/{Uri.EscapeDataString(id)}/run");
        throw new ArgumentException("Expected ResearchAgent model 'researchagent/research' or 'researchagent/agents/{id}'.", nameof(model));
    }

    private static bool IsValidId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private JsonElement BuildPayload(AIRequest request, string route)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var options) == true && options is not null)
        {
            var metadata = options is JsonElement json ? json : JsonSerializer.SerializeToElement(options, JsonOptions);
            if (metadata.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("ResearchAgent provider metadata must be a JSON object.", nameof(request));
            foreach (var property in metadata.EnumerateObject()) merged[property.Name] = property.Value.Clone();
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
            catch (JsonException)
            {
                if (route != "research")
                    throw new ArgumentException("ResearchAgent saved-agent input must contain a JSON object.", nameof(request));
                input = default;
            }
            if (input.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in input.EnumerateObject()) merged[property.Name] = property.Value.Clone();
            }
            else if (route == "research" && input.ValueKind == JsonValueKind.Undefined)
                merged["prompt"] = JsonSerializer.SerializeToElement(text);
            else
                throw new ArgumentException("ResearchAgent input must be a JSON object or a plain-text research prompt.", nameof(request));
        }

        if (route == "research" && (!merged.TryGetValue("prompt", out var prompt)
            || prompt.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prompt.GetString())))
            throw new ArgumentException("ResearchAgent research requires a non-empty prompt.", nameof(request));
        if (merged.Count == 0)
            throw new ArgumentException("ResearchAgent requires user input or provider-scoped JSON metadata.", nameof(request));
        return JsonSerializer.SerializeToElement(merged, JsonOptions);
    }

    private AIResponse MapResponse(string model, string route, JsonElement input, JsonElement raw)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["researchagent.raw"] = raw.Clone(), ["researchagent.route"] = route
        };
        foreach (var name in new[] { "model_used", "execution_time_seconds", "request_id", "agent_id", "agent_name" })
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value))
                metadata[$"researchagent.{name}"] = value.Clone();
        var part = new AIToolCallContentPart
        {
            Type = "tool-call", ToolCallId = GetString(raw, "request_id") ?? $"researchagent-{Guid.NewGuid():N}",
            ToolName = ToolName(route), Title = $"ResearchAgent {route}", Input = input.Clone(),
            Output = CreateToolResult(raw), ProviderExecuted = true, State = "output-available", Metadata = metadata
        };
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = model, Status = "completed",
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Content = [part], Metadata = metadata }],
                Metadata = metadata
            },
            Metadata = metadata
        };
    }

    private static string? GetString(JsonElement raw, string name)
        => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static CallToolResult CreateToolResult(JsonElement raw) => new()
    {
        Content = [], StructuredContent = raw.Clone()
    };

    private static string ToolName(string route) => route == "research" ? "researchagent_research" : "researchagent_run_agent";

    private AIStreamEvent Event(string type, string id, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null, AIOutput? output = null) => new()
    {
        ProviderId = GetIdentifier(),
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = timestamp, Output = output },
        Metadata = metadata
    };
}
