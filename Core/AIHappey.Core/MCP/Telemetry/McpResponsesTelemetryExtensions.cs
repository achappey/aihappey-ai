using AIHappey.Core.Contracts;
using AIHappey.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Core.MCP.Telemetry;

public static class McpResponsesTelemetryExtensions
{
    public static async Task TrackMcpResponsesTelemetryAsync(
        this IServiceProvider services,
        ResponseResult? response,
        IModelProvider provider,
        float temperature,
        DateTime startedAt,
        CancellationToken cancellationToken = default)
    {
        var handler = services.GetService<IMcpResponsesTelemetryHandler>();
        if (handler is null || response is null)
            return;

        var tokenCounts = McpResponsesUsageExtractor.GetTokenCounts(response.Usage);

        await handler.TrackResponsesAsync(
            new McpResponsesTelemetryContext(
                Model: NormalizeModel(response.Model),
                ProviderId: provider.GetIdentifier(),
                Temperature: temperature,
                InputTokens: tokenCounts.InputTokens,
                TotalTokens: tokenCounts.TotalTokens,
                StartedAt: startedAt,
                EndedAt: DateTime.UtcNow),
            cancellationToken);
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return model;

        var parts = model.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? parts[1] : model;
    }
}

