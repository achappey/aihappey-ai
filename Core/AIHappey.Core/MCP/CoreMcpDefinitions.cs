using AIHappey.Common.MCP;
using AIHappey.Core.MCP.Models;
using AIHappey.Core.MCP.Provider;

namespace AIHappey.Core.MCP;

public static class CoreMcpDefinitions
{
    public static IEnumerable<McpServerDefinition> GetDefinitions()
    {
        yield return new McpServerDefinition(
            Name: "AI-Models",
            Description: "List available AI models.",
            ToolTypes: [typeof(ModelTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Providers",
            Description: "Get AI providers, models and metadata info.",
            ToolTypes: [typeof(ProviderTools)]
        );
    }
}
