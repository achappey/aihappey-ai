
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

public class Tool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("defer_loading")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DeferLoading { get; set; }

    [JsonPropertyName("allowed_callers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? AllowedCallers { get; set; }

    [JsonPropertyName("inputSchema")]
    public ToolSchema? InputSchema { get; set; }

    [JsonPropertyName("outputSchema")]
    public ToolSchema? OutputSchema { get; set; }
}

public class ToolSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("properties")]
    public Dictionary<string, object>? Properties { get; set; }

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = [];
}
