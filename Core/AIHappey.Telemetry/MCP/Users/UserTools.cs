using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Telemetry.MCP.Users;

[McpServerToolType]
public class UserTools
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

    private static TopOrder ParseOrder(string? order) =>
        string.Equals(order, "tokens", StringComparison.OrdinalIgnoreCase) ? TopOrder.Tokens :
        string.Equals(order, "duration", StringComparison.OrdinalIgnoreCase) ? TopOrder.Duration
            : TopOrder.Requests;

    // -------------------------
    // TELEMETRY: Top Users
    // -------------------------
    [Description("Top users by requests, tokens or duration (seconds).")]
    [McpServerTool(Title = "Telemetry top users", Name = "ai_users_top_users",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_TopUsers(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default), 'tokens' or 'duration'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopUsersAsync(Range(startDateTimeUtc, endDateTimeUtc), Math.Max(1, top), ParseOrder(order), ct);
        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = res }, JsonSerializerOptions.Web)
        };
    }

    [Description("Exact user-centric window summary with optional identifier exclusions applied after lower-trim normalization. Use this for KPI totals instead of reconstructing totals from top-N rankings.")]
    [McpServerTool(Title = "User window summary", Name = "ai_users_window_summary",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_WindowSummary(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Optional identifiers to exclude after lower(trim(identifier)) normalization.")] string[]? excludeIdentifiers,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var stats = services.GetRequiredService<IChatStatisticsService>();
        var summary = await stats.GetUserWindowSummaryAsync(Range(startDateTimeUtc, endDateTimeUtc), excludeIdentifiers, ct);

         return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(summary, JsonSerializerOptions.Web)
        };
    }

    [Description("Paged, audit-safe per-user aggregates for an explicit UTC window. Includes raw username, normalized identifier candidate, email heuristics, requests, tokens and duration.")]
    [McpServerTool(Title = "User aggregates", Name = "ai_users_user_aggregates",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_UserAggregates(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Zero-based number of ranked rows to skip.")] int skip,
        [Description("Maximum number of rows to return. Values are clamped to 1..500.")] int take,
        [Description("Order by 'tokens', 'requests' or 'duration'.")] string? order,
        [Description("Optional identifiers to exclude after lower(trim(identifier)) normalization.")] string[]? excludeIdentifiers,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var stats = services.GetRequiredService<IChatStatisticsService>();
        var page = await stats.GetUserAggregatesAsync(Range(startDateTimeUtc, endDateTimeUtc), skip, take, ParseOrder(order), excludeIdentifiers, ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(page, JsonSerializerOptions.Web)
        };
    }

    [Description("Reconciles exact user totals against a top-N ranking for an explicit UTC window. Use this to prove whether a leaderboard is complete enough for KPI work.")]
    [McpServerTool(Title = "User aggregate reconciliation", Name = "ai_users_user_reconciliation",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_UserReconciliation(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        [Description("Top-N size to compare against the exact totals.")] int top,
        [Description("Order by 'tokens', 'requests' or 'duration'.")] string? order,
        [Description("Optional identifiers to exclude after lower(trim(identifier)) normalization.")] string[]? excludeIdentifiers,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var stats = services.GetRequiredService<IChatStatisticsService>();
        var reconciliation = await stats.GetUserAggregateReconciliationAsync(Range(startDateTimeUtc, endDateTimeUtc), top, ParseOrder(order), excludeIdentifiers, ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(reconciliation, JsonSerializerOptions.Web)
        };
    }

    [Description("Identifier quality diagnostics for telemetry users in an explicit UTC window, including email-likeness, domains, normalization collisions and non-email samples.")]
    [McpServerTool(Title = "User identifier health", Name = "ai_users_identifier_health",
        Idempotent = true, ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_IdentifierHealth(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var stats = services.GetRequiredService<IChatStatisticsService>();
        var health = await stats.GetIdentifierHealthAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(health, JsonSerializerOptions.Web)
        };
    }

    [Description("New users per day.")]
    [McpServerTool(Title = "New users per day",
        Name = "ai_users_new_users_per_day",
        Idempotent = true,
        ReadOnly = true, OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_NewUsersPerDay(
          [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
          [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
          IServiceProvider services,
          RequestContext<CallToolRequestParams> _,
          CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var data = await s.GetNewUsersPerDayAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        var payload = data.Select(d => new { day = d.Day.ToString("yyyy-MM-dd"), new_users = d.Count });

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = payload }, JsonSerializerOptions.Web)
        };
    }


    // -------------------------
    // Daily Active Users (DAU)
    // -------------------------
    [Description("Daily distinct active users over an explicit UTC datetime range.")]
    [McpServerTool(
        Title = "Daily active users",
        Name = "ai_users_daily_active_users",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_DailyActiveUsers(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var stats = services.GetRequiredService<IChatStatisticsService>();
        var data = await stats.GetDailyDistinctUsersAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        // -> [{ "day": "yyyy-MM-dd", "active_users": number }]
        var payload = data.Select(d => new
        {
            day = d.Day.ToString("yyyy-MM-dd"),
            active_users = d.Count
        });

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = payload }, JsonSerializerOptions.Web)
        };
    }

    // -------------------------
    // Cumulative Distinct Users (growth curve)
    // -------------------------
    [Description("Cumulative distinct users per day over an explicit UTC datetime range (growth curve).")]
    [McpServerTool(
        Title = "Cumulative distinct users",
        Name = "ai_users_cumulative_users",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIUsers_CumulativeUsers(
        [Description("Start of the telemetry window in UTC.")] DateTime startDateTimeUtc,
        [Description("Optional end of the telemetry window in UTC. Defaults to current UTC time when omitted.")] DateTime? endDateTimeUtc,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var stats = services.GetRequiredService<IChatStatisticsService>();
        var cumulative = await stats.GetCumulativeDistinctUsersAsync(Range(startDateTimeUtc, endDateTimeUtc), ct);

        // -> [{ "day": "yyyy-MM-dd", "cumulative_users": number }]
        var payload = cumulative.Select(d => new
        {
            day = d.Day.ToString("yyyy-MM-dd"),
            cumulative_users = d.Count
        });

        return new CallToolResult()
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { items = payload }, JsonSerializerOptions.Web)
        };
    }

}
