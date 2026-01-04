using AIHappey.Common.MCP;
using AIHappey.Telemetry.MCP.Models;
using AIHappey.Telemetry.MCP.Requests;
using AIHappey.Telemetry.MCP.Tools;
using AIHappey.Telemetry.MCP.Users;

namespace AIHappey.Telemetry.MCP;

public static class TelemetryMcpDefinitions
{
    public static IEnumerable<McpServerDefinition> GetDefinitions()
    {
        yield return new McpServerDefinition(
            Name: "AI-Telemetry-Users",
            Title: "AI Telemetry Users",
            Description: "Shows how people use AI and how often on {host}.",
            PromptTypes: [typeof(UserPrompts)],
            ToolTypes: [typeof(UserTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Telemetry-Models",
            Title: "AI Telemetry Models",
            Description: "Overview of all AI model usage on {host}.",
            PromptTypes: [typeof(ModelPrompts)],
            ToolTypes: [typeof(ModelTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Telemetry-Tools",
            Title: "AI Telemetry Tools",
            Description: "Displays active AI tools and usage on {host}.",
            PromptTypes: [typeof(ToolPrompts)],
            ToolTypes: [typeof(ToolTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Telemetry-Requests",
            Title: "AI Telemetry Requests",
            Description: "Tracks all AI activity and request types on {host}.",
            PromptTypes: [typeof(RequestPrompts)],
            ToolTypes: [typeof(RequestTools)]
        );
    }
}
