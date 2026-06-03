using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIHappey.Telemetry.MCP.Models;

[McpServerPromptType]
public class ModelPrompts
{
    [McpServerPrompt(Name = "top-models-recent", Title = "Top models recent"), Description("Identify most used models")]
    public static string TopModelsRecent() =>
        "Show the 20 most used models in the last month with their total token consumption. SHow them in a table randked on total tokens descending.";

    [McpServerPrompt(Name = "model-usage-graph", Title = "Model usage graph"), Description("Visualise model usage relationships")]
    public static string ModelUsageGraph() =>
        "Take the most used models from the past month and present them in a simple diagram or graph showing their relative usage.";

    [McpServerPrompt(Name = "model-users-ranking", Title = "Model users ranking"), Description("Rank users for a specific model")]
    public static string ModelUsersRanking(
        [Description("Exact model name to inspect, for example gpt-4o-mini.")] string model,
        [Description("The number of users to retrieve, for example 10, 20 or 50.")] string topXUsers,
        [Description("The number of past days to include, for example 14, 30 or 90.")] string days) =>
        $"Use the 'Telemetry top users for model' tool to rank the top {topXUsers} users of model '{model}' over the past {days} days. Order by total tokens unless the user asks for request count or duration. Present provider, model, username, telemetry user id, requests, input tokens, output tokens, total tokens and duration seconds.";

    [McpServerPrompt(Name = "provider-comparison-chart", Title = "Provider comparison chart"), Description("Compare providers visually")]
    public static string ProviderComparisonChart() =>
        "Compare total token usage between different providers over the past quarter as a stacked bar chart.";

    [McpServerPrompt(Name = "token-trend-per-provider", Title = "Token trend per provider"), Description("Token trend per provider")]
    public static string TokenTrendPerProvider() =>
        "Plot the daily total token consumption per provider for the last 30 days as multiple line charts on the same axes.";
}
