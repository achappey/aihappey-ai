
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model;

public class Tool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("inputSchema")]
    public ToolInputSchema? InputSchema { get; set; }
}

public class ToolInputSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("properties")]
    public Dictionary<string, object>? Properties { get; set; }

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = [];
}
