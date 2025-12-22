using System.ComponentModel;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Telemetry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.AzureAuth.MCP.Users;

[McpServerToolType]
public class UserTools
{
    // -------------------------
    // Helpers
    // -------------------------
    private static StatsWindow Days(int days) => StatsWindow.LastDaysUtc(days <= 0 ? 1 : days);

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
    public static async Task<ContentBlock?> AIUsers_TopUsers(
        [Description("Lookback window in days (UTC).")] int days,
        [Description("Max items to return.")] int top,
        [Description("Order by 'requests' (default), 'tokens' or 'duration'.")] string? order,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var res = await s.TopUsersAsync(Days(days), Math.Max(1, top), ParseOrder(order), ct);
        return new EmbeddedResourceBlock()
        {
            Resource = new TextResourceContents()
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://top/users",
                Text = JsonSerializer.Serialize(res, JsonSerializerOptions.Web)
            }
        };
    }

    [Description("New users per day.")]
    [McpServerTool(Title = "New users per day",
        Name = "ai_users_new_users_per_day",
        Idempotent = true,
        ReadOnly = true, OpenWorld = false)]
    public static async Task<ContentBlock?> AIUsers_NewUsersPerDay(
          [Description("Lookback window in days (UTC).")] int days,
          IServiceProvider services,
          RequestContext<CallToolRequestParams> _,
          CancellationToken ct = default)
    {
        var s = services.GetRequiredService<IChatStatisticsService>();
        var data = await s.GetNewUsersPerDayAsync(Days(days), ct);

        var payload = data.Select(d => new { day = d.Day.ToString("yyyy-MM-dd"), new_users = d.Count });

        return new EmbeddedResourceBlock
        {
            Resource = new TextResourceContents
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://users/new-per-day",
                Text = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)
            }
        };
    }


    // -------------------------
    // Daily Active Users (DAU)
    // -------------------------
    [Description("Daily distinct active users over a lookback window.")]
    [McpServerTool(
        Title = "Daily active users",
        Name = "ai_users_daily_active_users",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<ContentBlock?> AIUsers_DailyActiveUsers(
        [Description("Lookback window in days (UTC). Default 180 (≈6 months).")]
        int days,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        if (days <= 0) days = 180;

        var stats = services.GetRequiredService<IChatStatisticsService>();
        var data = await stats.GetDailyDistinctUsersAsync(Days(days), ct);

        // -> [{ "day": "yyyy-MM-dd", "active_users": number }]
        var payload = data.Select(d => new
        {
            day = d.Day.ToString("yyyy-MM-dd"),
            active_users = d.Count
        });

        return new EmbeddedResourceBlock
        {
            Resource = new TextResourceContents
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://users/daily-active",
                Text = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)
            }
        };
    }

    // -------------------------
    // Cumulative Distinct Users (growth curve)
    // -------------------------
    [Description("Cumulative distinct users per day over a lookback window (growth curve).")]
    [McpServerTool(
        Title = "Cumulative distinct users",
        Name = "ai_users_cumulative_users",
        Idempotent = true,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<ContentBlock?> AIUsers_CumulativeUsers(
        [Description("Lookback window in days (UTC). Default 180 (≈6 months).")]
        int days,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken ct = default)
    {
        if (days <= 0) days = 180;

        var stats = services.GetRequiredService<IChatStatisticsService>();
        var cumulative = await stats.GetCumulativeDistinctUsersAsync(Days(days), ct);

        // -> [{ "day": "yyyy-MM-dd", "cumulative_users": number }]
        var payload = cumulative.Select(d => new
        {
            day = d.Day.ToString("yyyy-MM-dd"),
            cumulative_users = d.Count
        });

        return new EmbeddedResourceBlock
        {
            Resource = new TextResourceContents
            {
                MimeType = MediaTypeNames.Application.Json,
                Uri = "ai://users/cumulative",
                Text = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)
            }
        };
    }

}

