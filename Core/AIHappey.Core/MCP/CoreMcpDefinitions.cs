using AIHappey.Common.MCP;
using AIHappey.Core.MCP.Models;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.MCP.Provider;
using AIHappey.Core.MCP.Realtime;
using AIHappey.Core.MCP.Rerank;

namespace AIHappey.Core.MCP;

public static class CoreMcpDefinitions
{
    public static IEnumerable<McpServerDefinition> GetDefinitions()
    {
        yield return new McpServerDefinition(
            Name: "AI-Models",
            Title: "AI Models",
            Description: "List available AI models.",
            ToolTypes: [typeof(ModelTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Providers",
            Title: "AI Providers",
            Description: "Get AI providers, models and metadata info.",
            ToolTypes: [typeof(ProviderTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Images",
            Title: "AI Images",
            Description: "Generate images using the unified image endpoint.",
            ToolTypes: [typeof(ImageTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Speech",
            Title: "AI Speech",
            Description: "Generate speech audio using the unified speech endpoint.",
            ToolTypes: [typeof(SpeechTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Transcriptions",
            Title: "AI Transcriptions",
            Description: "Create audio transcriptions using the unified endpoint.",
            ToolTypes: [typeof(TranscriptionTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Realtime",
            Title: "AI Realtime",
            Description: "Get realtime tokens/sessions using the unified endpoint.",
            ToolTypes: [typeof(RealtimeTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-Rerank",
            Title: "AI Rerank",
            Description: "Rerank documents using the unified endpoint.",
            ToolTypes: [typeof(RerankingTools)]
        );
    }
}
