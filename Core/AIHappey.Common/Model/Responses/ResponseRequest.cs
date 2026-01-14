
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

public class ResponseRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("text")]
    public object? Text { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("input")]
    public IEnumerable<object> Input { get; set; } = [];

    [JsonPropertyName("tools")]
    public IEnumerable<object> Tools { get; set; } = [];

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, Dictionary<string, object>>? Metadata { get; set; }

}
