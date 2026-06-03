using AIHappey.AzureAuth.Extensions;
using AIHappey.Core.MCP.Telemetry;
using AIHappey.Telemetry;
using AIHappey.Telemetry.Models;

namespace AIHappey.AzureAuth;

public sealed class AzureAuthMcpResponsesTelemetryHandler(
    IChatTelemetryService chatTelemetryService,
    IHttpContextAccessor httpContextAccessor) : IMcpResponsesTelemetryHandler
{
    public async Task TrackResponsesAsync(
        McpResponsesTelemetryContext context,
        CancellationToken cancellationToken = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var userId = httpContext?.GetUserOid();
        var username = httpContext?.GetUserUpn();

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(username))
            return;

        await chatTelemetryService.TrackChatRequestAsync(
            new Vercel.Models.ChatRequest
            {
                Model = context.Model ?? "unknown",
                Temperature = context.Temperature,
            },
            userId,
            username,
            context.InputTokens,
            context.TotalTokens,
            context.ProviderId,
            RequestType.Responses,
            context.StartedAt,
            context.EndedAt,
            cancellationToken: cancellationToken);
    }
}

