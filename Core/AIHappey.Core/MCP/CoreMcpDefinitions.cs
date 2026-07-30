using AIHappey.Common.MCP;
using AIHappey.Core.Contracts;
using AIHappey.Core.MCP.Inference;
using AIHappey.Core.MCP.Models;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.MCP.Provider;
using AIHappey.Core.MCP.Realtime;
using AIHappey.Core.MCP.Rerank;
using AIHappey.Core.MCP.WebSearch;

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

        yield return new McpServerDefinition(
            Name: "AI-Inference",
            Title: "AI Inference",
            Description: "Execute AI inference requests using the unified responses endpoint.",
            ToolTypes: [typeof(InferenceTools)]
        );

        yield return new McpServerDefinition(
            Name: "AI-WebSearch",
            Title: "AI Web Search",
            Description: "Search the web using internal AI model providers and the unified responses endpoint.",
            ToolTypes: [typeof(WebSearchTools)]
        );

        foreach (var definition in GetProviderDefinitions())
            yield return definition;
    }

    private static IEnumerable<McpServerDefinition> GetProviderDefinitions()
    {
        var providerTypes = typeof(CoreMcpDefinitions).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(IProviderMcpServers).IsAssignableFrom(type));

        var definitions = providerTypes
            .SelectMany(type => type
                .GetCustomAttributes(typeof(McpServerAttribute), inherit: false)
                .Cast<McpServerAttribute>()
                .Select(attribute => CreateProviderDefinition(type, attribute)))
            .ToArray();

        var duplicates = definitions
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
            throw new InvalidOperationException($"Duplicate provider MCP server names: {string.Join(", ", duplicates)}.");

        return definitions.OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static McpServerDefinition CreateProviderDefinition(Type providerType, McpServerAttribute attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Description);

        if (attribute.ToolMethodNames.Count == 0 || attribute.ToolMethodNames.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Provider MCP server '{attribute.Name}' must select at least one valid tool method.");

        return new McpServerDefinition(
            Name: attribute.Name,
            Description: attribute.Description,
            Title: attribute.Title,
            ToolTypes: [providerType],
            ToolMethodNames: [.. attribute.ToolMethodNames],
            Icons: attribute.Icons);
    }
}
