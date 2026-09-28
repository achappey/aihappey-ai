using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.IOnet;

public partial class IOnetProvider
{
    private const string AgentPrefix = "agents/";
    private const string WorkflowToolName = "ionet_workflow_result";

    private static bool IsAgentModel(string? model)
    {
        var local = model?.Trim() ?? "";
        if (local.StartsWith("ionet/", StringComparison.OrdinalIgnoreCase)) local = local[6..];
        return local.StartsWith(AgentPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string AgentName(string? model)
    {
        var local = model?.Trim() ?? "";
        if (local.StartsWith("ionet/", StringComparison.OrdinalIgnoreCase)) local = local[6..];
        var name = local[AgentPrefix.Length..];
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
            throw new ArgumentException($"Invalid io.net agent model '{model}'.", nameof(model));
        return name;
    }

    private static JsonElement AsJson(object value)
        => value is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static JsonObject AgentOptions(AIRequest request)
    {
        object? scoped = null;
        if (request.Metadata?.TryGetValue("ionet", out scoped) != true || scoped is null)
        {
            if (request.Metadata?.TryGetValue("chatcompletions.request.metadata", out var chatMetadata) == true
                && chatMetadata is not null)
            {
                var chat = AsJson(chatMetadata);
                if (chat.ValueKind == JsonValueKind.Object && chat.TryGetProperty("ionet", out var nested))
                    scoped = nested.Clone();
            }
        }
        if (scoped is null) return [];
        return JsonNode.Parse(AsJson(scoped).GetRawText()) as JsonObject
            ?? throw new ArgumentException("io.net provider metadata must be an object.");
    }

    private static JsonObject BuildWorkflowBody(AIRequest request, string name)
    {
        if (request.Tools is { Count: > 0 } || request.ToolChoice is not null || request.ResponseFormat is not null)
            throw new NotSupportedException("io.net workflows do not support client tools or response formats.");

        var user = request.Input?.Items?.AsEnumerable().Reverse()
            .FirstOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (user?.Content?.Any(part => part is not AITextContentPart) == true)
            throw new NotSupportedException("io.net workflows only accept text input.");
        var text = user is null ? request.Input?.Text : string.Join("\n", user.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("io.net workflows require a user text input.", nameof(request));

        var body = new JsonObject { ["objective"] = request.Instructions, ["text"] = text,
            ["agent_names"] = new JsonArray(JsonValue.Create(name)) };
        var options = AgentOptions(request);
        foreach (var key in new[] { "objective", "persona", "args", "agent_names" })
            if (options.TryGetPropertyValue(key, out var value)) body[key] = value?.DeepClone();

        if (body["agent_names"] is not JsonArray { Count: > 0 } agents || agents.Any(agent => agent is not JsonValue v
            || !v.TryGetValue<string>(out var agentName) || string.IsNullOrWhiteSpace(agentName)))
            throw new ArgumentException("io.net agent_names must be a non-empty array of names.");
        if (body["objective"] is not null && (body["objective"] is not JsonValue objective || !objective.TryGetValue<string>(out _)))
            throw new ArgumentException("io.net objective must be a string or null.");
        foreach (var key in new[] { "persona", "args" })
            if (body[key] is not null && body[key] is not JsonObject)
                throw new ArgumentException($"io.net {key} must be an object or null.");
        return body;
    }

    private async Task<AIResponse> ExecuteAgentAsync(AIRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = AgentName(request.Model);
        var body = BuildWorkflowBody(request, name);
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No IOnet API key.");

        using var http = new HttpRequestMessage(HttpMethod.Post, "v1/workflows/run")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"io.net workflow error ({(int)response.StatusCode}): {payload}", null, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        var raw = document.RootElement.Clone();
        var result = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("result", out var value)
            ? value.Clone() : raw.Clone();
        var trace = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("trace_id", out var traceId)
            && traceId.ValueKind == JsonValueKind.String ? traceId.GetString() : null;
        var metadata = new Dictionary<string, object?> { ["ionet.raw"] = raw, ["ionet.result"] = result };
        if (trace is not null) metadata["ionet.trace_id"] = trace;

        var tool = new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolName = WorkflowToolName, ToolCallId = $"ionet-workflow-{trace ?? Guid.NewGuid().ToString("N")}",
            Title = "io.net workflow result", ProviderExecuted = true, State = "output-available",
            Input = new { agent_names = body["agent_names"]?.DeepClone() },
            Output = new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(new { result }) },
            Metadata = metadata
        };
        var display = result.ValueKind == JsonValueKind.String ? result.GetString() ?? "null" : result.GetRawText();
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"{AgentPrefix}{name}".ToModelId(GetIdentifier()), Status = "completed",
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Content = [tool,
                    new AITextContentPart { Type = "text", Text = display, Metadata = metadata }], Metadata = metadata }],
                Metadata = metadata
            },
            Metadata = metadata
        };
    }

    private IEnumerable<AIStreamEvent> AgentEvents(AIResponse response)
    {
        var parts = response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [];
        foreach (var tool in parts.OfType<AIToolCallContentPart>())
        {
            yield return AgentEvent("tool-input-available", tool.ToolCallId,
                new AIToolInputAvailableEventData { ToolName = tool.ToolName!, Input = tool.Input ?? new { }, ProviderExecuted = true }, response.Metadata);
            yield return AgentEvent("tool-output-available", tool.ToolCallId,
                new AIToolOutputAvailableEventData { ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true }, response.Metadata);
        }
        foreach (var text in parts.OfType<AITextContentPart>())
        {
            var id = Guid.NewGuid().ToString("N");
            yield return AgentEvent("text-start", id, new AITextStartEventData(), response.Metadata);
            yield return AgentEvent("text-delta", id, new AITextDeltaEventData { Delta = text.Text }, response.Metadata);
            yield return AgentEvent("text-end", id, new AITextEndEventData(), response.Metadata);
        }
        yield return AgentEvent("finish", null, new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, DateTimeOffset.UtcNow, null,
                additionalProperties: new Dictionary<string, object?> { ["ionet"] = response.Metadata ?? [] })
        }, response.Metadata);
    }

    private AIStreamEvent AgentEvent(string type, string? id, object data, Dictionary<string, object?>? metadata)
        => new() { ProviderId = GetIdentifier(), Event = new AIEventEnvelope
            { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata }, Metadata = metadata };
}
