using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIHappey.Telemetry.MCP.Users;

[McpServerPromptType]
public class UserPrompts
{
    [McpServerPrompt(Name = "top-users-recent", Title = "Top users recent"),
        Description("Retrieve and rank the most active users over a configurable time window.")]
    public static string TopUsersRecent(
        [Description("The number of users to retrieve (e.g., 10, 20, 50)")] string topXUsers,
        [Description("The number of past days to include (e.g., 14 for two weeks, 30 for one month)")] string days) =>
        $"First call the 'User window summary' tool for the past {days} days to get exact totals. Then call the 'User aggregate reconciliation' tool to verify whether top-{topXUsers} is complete enough for leaderboard-only use. Finally call the 'User aggregates' tool ordered by duration, requests and tokens as needed, and present the top {topXUsers} users. Do not reconstruct exact totals from a top-N leaderboard alone.";

    [McpServerPrompt(Name = "user-growth-chart", Title = "User growth chart"), Description("Track growth of distinct users")]
    public static string UserGrowthChart() =>
        "Show how the number of distinct active users has changed over the last 6 months as a line chart.";

    [McpServerPrompt(Name = "tokens-per-user-chart", Title = "Tokens per user chart"), Description("Token consumption per user")]
    public static string TokensPerUserChart() =>
        "Use the 'User window summary' tool for exact monthly totals, then use the 'User aggregates' tool ordered by tokens to build a horizontal bar chart ranking users from highest to lowest. Prefer the paged aggregate endpoint over a top-N leaderboard when exact reconciliation matters.";

    [McpServerPrompt(Name = "new-users-per-day", Title = "New users per day"), Description("Monitor onboarding of new users")]
    public static string NewUsersPerDay() =>
        "Count how many new users appeared each day over the last 60 days and display it as a bar chart.";

    [Description("Find active users grouped by department and visualize usage")]
    [McpServerPrompt(Name = "active-users-by-department",
        Title = "Active users by department")]
    public static string ActiveUsersByDepartment() =>
        "First use the 'User window summary' tool to capture exact totals for the last month. Then use the 'User aggregates' tool to fetch a paged, audit-safe user list with normalized identifiers and duration. Next use the 'User identifier health' tool to identify non-email or collision risks before calling the 'Group users by department' tool (with includeEmpty = false) for memberType 'User'. Build the department visualisation only after the identifier layer is trustworthy; otherwise return a clearly marked user-level fallback.";

    [McpServerPrompt(Name = "user-audit-reconciliation", Title = "User audit reconciliation"),
        Description("Prove whether a top-N user ranking is safe to use for exact totals.")]
    public static string UserAuditReconciliation(
        [Description("The number of top users to compare, for example 50, 100 or 200.")] string topXUsers,
        [Description("The number of past days to include.")] string days) =>
        $"Use the 'User window summary' tool over the past {days} days for exact totals. Then call the 'User aggregate reconciliation' tool with top={topXUsers} and order='tokens'. If the ranking is not complete, use the 'User aggregates' tool with paging to inspect the full population instead of trusting top-N totals.";

}
