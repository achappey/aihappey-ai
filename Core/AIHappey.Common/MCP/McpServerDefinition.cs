namespace AIHappey.Common.MCP;

public record McpServerDefinition(
    string Name,
    string? Description,
    string? Title,
    Type[]? PromptTypes = null,
    Type[]? ToolTypes = null);
