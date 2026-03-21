using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Telemetry.MCP.Requests;

[McpServerToolType]
public class RequestTools
{
    // -------------------------
    // Helpers
    // -------------------------
    private static StatsWindow Range(DateTime startDateTimeUtc, DateTime? endDateTimeUtc)
    {
        if (startDateTimeUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("startDateTimeUtc must be provided in UTC.", nameof(startDateTimeUtc));

        var end = endDateTimeUtc ?? DateTime.UtcNow;
        if (end.Kind != DateTimeKind.Utc)
            throw new ArgumentException("endDateTimeUtc must be provided in UTC when specified.", nameof(endDateTimeUtc));

        if (end <= startDateTimeUtc)
            throw new ArgumentException("endDateTimeUtc must be greater than startDateTimeUtc.", nameof(endDateTimeUtc));

        return new StatsWindow(startDateTimeUtc, end);
    }

    // -------------------------
    // TELEMETRY: Overview
    // -------------------------
    [Description("High-level telemetry: requests, users, tools, models, avg latency, token sums.")]
    [McpServerTool(Title = "Telemetry overview", Name = "ai_requests_overview",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIRequests_Overview(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetOverviewAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };

    }

    // -------------------------
    // TELEMETRY: Daily Activity
    // -------------------------
    [Description("Daily buckets: requests, unique users, total tokens.")]
    [McpServerTool(Title = "Telemetry daily activity", Name = "ai_requests_daily_activity",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIRequests_DailyActivity(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetDailyActivityAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = res }, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Request Type Breakdown
    // -------------------------
    [Description("Counts per request type (Chat, Sampling, Completion).")]
    [McpServerTool(Title = "Telemetry request types", Name = "ai_requests_request_types",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIRequests_RequestTypes(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetRequestTypesAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Token Stats
    // -------------------------
    [Description("Token stats: min, p50, p95, max, average for input & total.")]
    [McpServerTool(Title = "Telemetry token stats", Name = "ai_requests_token_stats",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIRequests_TokenStats(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetTokenStatsAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // TELEMETRY: Latency Stats
    // -------------------------
    [Description("Latency stats: min, p50, p95, max, average (ms).")]
    [McpServerTool(Title = "Telemetry latency stats", Name = "ai_requests_latency_stats",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIRequests_LatencyStats(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.GetLatencyStatsAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(res, JsonSerializerOptions.Web)
        };
    }
}
