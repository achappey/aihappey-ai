using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Telemetry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.AzureAuth.MCP.Requests;

[McpServerToolType]
public class RequestTools
{
    // -------------------------
    // Helpers
    // -------------------------
    private static StatsWindow Days(int days) => StatsWindow.LastDaysUtc(days <= 0 ? 1 : days);

    // -------------------------
    // TELEMETRY: Overview
    // -------------------------
    [Description("High-level telemetry: requests, users, tools, models, avg latency, token sums.")]
    [McpServerTool(Title = "Telemetry overview", Name = "ai_requests_overview",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIRequests_Overview(
        [Description("Lookback window in days (UTC).")] int days,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetOverviewAsync(Days(days), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://overview",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }

    // -------------------------
    // TELEMETRY: Daily Activity
    // -------------------------
    [Description("Daily buckets: requests, unique users, total tokens.")]
    [McpServerTool(Title = "Telemetry daily activity", Name = "ai_requests_daily_activity",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIRequests_DailyActivity(
        [Description("Lookback window in days (UTC).")] int days,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetDailyActivityAsync(Days(days), ct);

        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://activity",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };

    }

    // -------------------------
    // TELEMETRY: Request Type Breakdown
    // -------------------------
    [Description("Counts per request type (Chat, Sampling, Completion).")]
    [McpServerTool(Title = "Telemetry request types", Name = "ai_requests_request_types",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIRequests_RequestTypes(
        [Description("Lookback window in days (UTC).")] int days,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetRequestTypesAsync(Days(days), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://requestTypes",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }

    // -------------------------
    // TELEMETRY: Token Stats
    // -------------------------
    [Description("Token stats: min, p50, p95, max, average for input & total.")]
    [McpServerTool(Title = "Telemetry token stats", Name = "ai_requests_token_stats",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIRequests_TokenStats(
        [Description("Lookback window in days (UTC).")] int days,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetTokenStatsAsync(Days(days), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://tokens",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }

    // -------------------------
    // TELEMETRY: Latency Stats
    // -------------------------
    [Description("Latency stats: min, p50, p95, max, average (ms).")]
    [McpServerTool(Title = "Telemetry latency stats", Name = "ai_requests_latency_stats",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIRequests_LatencyStats(
        [Description("Lookback window in days (UTC).")] int days,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetLatencyStatsAsync(Days(days), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://latency",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }
}
