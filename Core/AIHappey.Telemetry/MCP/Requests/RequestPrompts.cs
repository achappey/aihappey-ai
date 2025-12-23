using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIHappey.Telemetry.MCP.Requests;

[McpServerPromptType]
public class RequestPrompts
{
    [McpServerPrompt(Name = "usage-overview", Title = "Usage overview"), Description("Get a quick overview of recent usage")]
    public static string UsageOverview() =>
        "Show me how many requests, users, and tokens there have been over the last 7 days.";

    [McpServerPrompt(Name = "daily-activity-chart", Title = "Daily activity chart"), Description("Visualise daily activity")]
    public static string DailyActivityChart() =>
        "Get daily activity for the last 30 days and draw a line chart with the date on the X-axis and number of requests on the Y-axis.";

    [McpServerPrompt(Name = "token-distribution-chart", Title = "Token distribution chart"), Description("Visualise token usage distribution")]
    public static string TokenDistributionChart() =>
        "Retrieve token statistics for the past 30 days and display them as a bar chart showing minimum, median, 95th percentile and maximum for input and total tokens.";

    [McpServerPrompt(Name = "latency-histogram", Title = "Latency histogram"), Description("Visualise response times")]
    public static string LatencyHistogram() =>
        "Fetch latency data for the last 7 days and create a histogram of response times with an indication of the average.";

    [McpServerPrompt(Name = "requests-by-type-chart", Title = "Requests by type chart"), Description("Breakdown of request types")]
    public static string RequestsByTypeChart() =>
        "Summarise the proportion of different request types in the last 30 days and display them as a pie chart.";

    [McpServerPrompt(Name = "response-time-trend", Title = "Response time trend"), Description("Trend of average response time")]
    public static string ResponseTimeTrend() =>
        "Plot the trend of average response times over the last 30 days as a line chart with time on the X-axis and milliseconds on the Y-axis.";

    [McpServerPrompt(Name = "hourly-request-pattern", Title = "Hourly request pattern"), Description("See which hours are busiest")]
    public static string HourlyRequestPattern() =>
        "Show how requests are distributed across the hours of the day for the past two weeks as a bar chart with hours on the X-axis and request counts on the Y-axis.";

    [McpServerPrompt(Name = "peak-vs-offpeak-requests", Title = "Peak vs off-peak requests"), Description("Compare peak and off-peak load")]
    public static string PeakVsOffpeakRequests() =>
        "Compare the number of requests sent during peak hours vs off-peak hours over the past month as a grouped bar chart.";

    [McpServerPrompt(Name = "tokens-per-request-boxplot", Title = "Tokens per request boxplot"), Description("Distribution per request")]
    public static string TokensPerRequestBoxplot() =>
        "Show the distribution of total tokens per request in the past two weeks as a box plot indicating quartiles and outliers.";

    [McpServerPrompt(Name = "request-type-stack", Title = "Request type stack"), Description("Stacked view of request types over time")]
    public static string RequestTypeStack() =>
        "Display how the proportions of different request types changed day by day in the last month as a stacked area chart.";
}
