namespace AIHappey.Core.MCP.Telemetry;

public interface IMcpResponsesTelemetryHandler
{
    Task TrackResponsesAsync(McpResponsesTelemetryContext context, CancellationToken cancellationToken = default);
}

public sealed record McpResponsesTelemetryContext(
    string? Model,
    string ProviderId,
    float Temperature,
    int InputTokens,
    int TotalTokens,
    DateTime StartedAt,
    DateTime EndedAt);

