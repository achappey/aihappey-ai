
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

public class ResponseResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "response";

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("output")]
    public IEnumerable<object> Output { get; set; } = [];

    [JsonPropertyName("usage")]
    public object? Usage { get; set; }

    [JsonPropertyName("tools")]
    public IEnumerable<object> Tools { get; set; } = [];

    [JsonPropertyName("reasoning")]
    public object? Reasoning { get; set; }

    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

}
