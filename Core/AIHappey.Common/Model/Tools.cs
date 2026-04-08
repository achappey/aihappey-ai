
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model;

public class Tool2
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("inputSchema")]
    public ToolInputSchema2? InputSchema { get; set; }
}

public class ToolInputSchema2
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("properties")]
    public Dictionary<string, object>? Properties { get; set; }

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = [];
}
