using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIHappey.AzureAuth.MCP.Users;

[McpServerPromptType]
public class UserPrompts
{
    [McpServerPrompt(Name = "top-users-recent", Title = "Top users recent"),
        Description("Retrieve and rank the most active users over a configurable time window.")]
    public static string TopUsersRecent(
        [Description("The number of users to retrieve (e.g., 10, 20, 50)")] string topXUsers,
        [Description("The number of past days to include (e.g., 14 for two weeks, 30 for one month)")] string days) =>
        $"Run three queries over the past {days} days: (1) top {topXUsers} users by total duration, (2) top {topXUsers} users by request count, (3) top {topXUsers} users by tokens used. Merge results so the final list is ranked by duration (format duration as mm:ss), enriched with request and token counts.";

    [McpServerPrompt(Name = "user-growth-chart", Title = "User growth chart"), Description("Track growth of distinct users")]
    public static string UserGrowthChart() =>
        "Show how the number of distinct active users has changed over the last 6 months as a line chart.";

    [McpServerPrompt(Name = "tokens-per-user-chart", Title = "Tokens per user chart"), Description("Token consumption per user")]
    public static string TokensPerUserChart() =>
        "Compute the average total tokens per user for the past month and show it as a horizontal bar chart ranking users from highest to lowest.";

    [McpServerPrompt(Name = "new-users-per-day", Title = "New users per day"), Description("Monitor onboarding of new users")]
    public static string NewUsersPerDay() =>
        "Count how many new users appeared each day over the last 60 days and display it as a bar chart.";

    [Description("Find active users grouped by department and visualize usage")]
    [McpServerPrompt(Name = "active-users-by-department",
        Title = "Active users by department")]
    public static string ActiveUsersByDepartment() =>
        "Find the top 100 active users from the last month. Then use the 'Group users by department' tool (with includeEmpty = false) to fetch department data for memberType 'User'. Create a pie chart showing total duration per department. If no department data is available, return the top 50 users with their total duration in plain text.";

}
