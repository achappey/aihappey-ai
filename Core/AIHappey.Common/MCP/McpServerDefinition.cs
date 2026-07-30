using ModelContextProtocol.Protocol;

namespace AIHappey.Common.MCP;

public record McpServerDefinition(
    string Name,
    string? Description,
    string? Title,
    Type[]? PromptTypes = null,
    Type[]? ToolTypes = null,
    string[]? ToolMethodNames = null,
    Icon[]? Icons = null);
