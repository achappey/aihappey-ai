using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIHappey.Telemetry.MCP.Agents;

[McpServerPromptType]
public class AgentPrompts
{
    [McpServerPrompt(Name = "agent-usage-overview", Title = "Agent usage overview"), Description("Get a quick overview of recent agent usage")]
    public static string AgentUsageOverview() =>
        "Show me how many agent requests, active agents, users, models, and tokens there have been over the last 7 days. Ignore requests without an agent id.";

    [McpServerPrompt(Name = "top-agents-recent", Title = "Top agents recent"), Description("Identify most used agents")]
    public static string TopAgentsRecent() =>
        "Show the 20 most used agents in the last month with request count, total token consumption and duration seconds. Show them in a table ranked on total tokens descending.";

    [McpServerPrompt(Name = "agent-daily-activity-chart", Title = "Agent daily activity chart"), Description("Visualise daily agent activity")]
    public static string AgentDailyActivityChart() =>
        "Get daily agent activity for the last 30 days and draw a line chart with the date on the X-axis and number of agent requests on the Y-axis. Ignore requests without an agent id.";

    [McpServerPrompt(Name = "agent-models-ranking", Title = "Agent models ranking"), Description("Rank models used by a specific agent")]
    public static string AgentModelsRanking(
        [Description("Exact agent id to inspect.")] string agentId,
        [Description("The number of models to retrieve, for example 10, 20 or 50.")] string topXModels,
        [Description("The number of past days to include, for example 14, 30 or 90.")] string days) =>
        $"Use the 'Agent top models' tool to rank the top {topXModels} models for agent '{agentId}' over the past {days} days. Order by total tokens unless the user asks for request count or duration. Present agent id, provider, model, requests, input tokens, output tokens, total tokens and duration seconds.";

    [McpServerPrompt(Name = "agent-users-ranking", Title = "Agent users ranking"), Description("Rank users for a specific agent")]
    public static string AgentUsersRanking(
        [Description("Exact agent id to inspect.")] string agentId,
        [Description("The number of users to retrieve, for example 10, 20 or 50.")] string topXUsers,
        [Description("The number of past days to include, for example 14, 30 or 90.")] string days) =>
        $"Use the 'Agent top users' tool to rank the top {topXUsers} users of agent '{agentId}' over the past {days} days. Order by total tokens unless the user asks for request count or duration. Present agent id, username, telemetry user id, requests, input tokens, output tokens, total tokens and duration seconds.";

    [McpServerPrompt(Name = "agent-request-types-chart", Title = "Agent request types chart"), Description("Breakdown of agent request types")]
    public static string AgentRequestTypesChart() =>
        "Summarise the proportion of different request types for agent requests in the last 30 days and display them as a pie chart. Ignore requests without an agent id.";

    [McpServerPrompt(Name = "agent-token-distribution-chart", Title = "Agent token distribution chart"), Description("Visualise agent token usage distribution")]
    public static string AgentTokenDistributionChart() =>
        "Retrieve token statistics for agent requests over the past 30 days and display them as a bar chart showing minimum, median, 95th percentile and maximum for input and total tokens. Ignore requests without an agent id.";

    [McpServerPrompt(Name = "agent-latency-histogram", Title = "Agent latency histogram"), Description("Visualise agent response times")]
    public static string AgentLatencyHistogram() =>
        "Fetch latency data for agent requests in the last 7 days and create a histogram of response times with an indication of the average. Ignore requests without an agent id.";
}
