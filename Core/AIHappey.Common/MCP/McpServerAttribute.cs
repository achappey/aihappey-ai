using ModelContextProtocol.Protocol;

namespace AIHappey.Common.MCP;

/// <summary>
/// Declares an MCP server contributed by a provider type. Tool method names are
/// explicitly selected so one provider can safely contribute independent servers.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class McpServerAttribute(
    string name,
    string title,
    string description,
    string[]? icons = null,
    params string[] toolMethodNames) : Attribute
{
    public string Name { get; } = name;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public Icon[]? Icons { get; } = icons?.Select(a => new Icon()
    {
        Source = a,
        Theme = icons.Length > 1
            ? a.Contains("dark", StringComparison.OrdinalIgnoreCase) ? "dark"
            : a.Contains("light", StringComparison.OrdinalIgnoreCase) ? "light" 
            : null : null
    }).ToArray();

    public IReadOnlyList<string> ToolMethodNames { get; } = toolMethodNames;
}
