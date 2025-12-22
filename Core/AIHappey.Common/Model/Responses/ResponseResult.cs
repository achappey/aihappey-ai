
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

public class ResponseResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "response";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("output")]
    public IEnumerable<object> Output { get; set; } = [];

    [JsonPropertyName("usage")]
    public object? Usage { get; set; }
}
