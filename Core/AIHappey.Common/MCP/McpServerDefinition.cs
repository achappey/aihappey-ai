namespace AIHappey.Common.MCP;

public record McpServerDefinition(
    string Name,
    string? Description,
    Type[]? PromptTypes = null,
    Type[]? ToolTypes = null);
