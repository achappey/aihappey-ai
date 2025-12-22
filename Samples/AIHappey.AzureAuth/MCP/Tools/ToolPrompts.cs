using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIHappey.AzureAuth.MCP.Tools;

[McpServerPromptType]
public class ToolPrompts
{
    [McpServerPrompt(Name = "tool-usage-heatmap", Title = "Tool usage heatmap"), Description("Heatmap of tool usage by day")]
    public static string ToolUsageHeatmap() =>
        "Take daily counts of tool usage over the past month and present them as a heatmap with days on one axis and tools on the other.";

    [McpServerPrompt(Name = "tool-usage-treemap", Title = "Tool usage treemap"), Description("Visualise tool usage share")]
    public static string ToolUsageTreemap() =>
        "Illustrate the relative share of each tool used in the past month as a treemap chart where size corresponds to usage.";
}
