using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIHappey.AzureAuth.MCP.Models;

[McpServerPromptType]
public class ModelPrompts
{
    [McpServerPrompt(Name = "top-models-recent", Title = "Top models recent"), Description("Identify most used models")]
    public static string TopModelsRecent() =>
        "Show the 20 most used models in the last month with their total token consumption. SHow them in a table randked on total tokens descending.";

    [McpServerPrompt(Name = "model-usage-graph", Title = "Model usage graph"), Description("Visualise model usage relationships")]
    public static string ModelUsageGraph() =>
        "Take the most used models from the past month and present them in a simple diagram or graph showing their relative usage.";

    [McpServerPrompt(Name = "provider-comparison-chart", Title = "Provider comparison chart"), Description("Compare providers visually")]
    public static string ProviderComparisonChart() =>
        "Compare total token usage between different providers over the past quarter as a stacked bar chart.";

    [McpServerPrompt(Name = "token-trend-per-provider", Title = "Token trend per provider"), Description("Token trend per provider")]
    public static string TokenTrendPerProvider() =>
        "Plot the daily total token consumption per provider for the last 30 days as multiple line charts on the same axes.";
}
