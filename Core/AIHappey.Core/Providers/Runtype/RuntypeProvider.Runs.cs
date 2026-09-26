using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Runtype;

public sealed partial class RuntypeProvider
{
    private async Task<JsonReply> ListAgentsPage(JsonObject options, CancellationToken ct)
    {
        var query = new List<string>();
        foreach (var name in new[] { "limit", "cursor", "includeCount", "search", "agentType", "managedInCode", "view" })
            if (options[name] is JsonValue value)
                query.Add($"{name}={Uri.EscapeDataString(value.ToString())}");
        return await SendJson(HttpMethod.Get, "v1/agents" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, ct);
    }

    private async Task<AIResponse> ReadRunAsync(Route route, AIRequest request, JsonObject options, CancellationToken ct)
    {
        var id = RequiredId(options, request, "executionId");
        var path = route.Kind == RouteKind.Status
            ? $"v1/executions/{Uri.EscapeDataString(id)}/status"
            : $"v1/agents/{Uri.EscapeDataString(route.AgentId!)}/executions/{Uri.EscapeDataString(id)}";
        return CreateOperationResponse(route, route.Kind == RouteKind.Status ? "get_runtype_execution_status" : "get_runtype_execution",
            await SendJson(HttpMethod.Get, path, null, ct), id);
    }

    private AIResponse CreateOperationResponse(Route route, string toolName, JsonReply reply, string? executionId = null)
    {
        var raw = reply.Body;
        var metadata = Metadata(raw, headers: reply.Headers);
        metadata["runtype.httpStatus"] = reply.HttpStatus;
        if (executionId is not null) metadata["runtype.executionId"] = executionId;
        var tool = new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = $"runtype-{toolName}-{executionId ?? Guid.NewGuid().ToString("N")}",
            ToolName = toolName, Title = toolName, Input = executionId is null ? new { } : new { executionId },
            Output = new CallToolResult { StructuredContent = raw.Clone() }, ProviderExecuted = true,
            State = "output-available", Metadata = metadata
        };
        var content = new List<AIContentPart> { tool };
        var final = Property(raw, "finalOutput") ?? Property(raw, "result");
        if (final is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } value)
            content.Add(new AITextContentPart { Type = "text", Text = value, Metadata = metadata });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = route.Model, Status = String(raw, "status") ?? "completed",
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }], Metadata = metadata },
            Usage = Usage(raw), Metadata = metadata
        };
    }
}
