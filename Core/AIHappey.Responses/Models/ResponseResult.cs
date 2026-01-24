
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

public class ResponseResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "response";

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public long? CompletedAt { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("output")]
    public IEnumerable<object> Output { get; set; } = [];

    [JsonPropertyName("usage")]
    public object? Usage { get; set; }

    [JsonPropertyName("text")]
    public object? Text { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; }

    [JsonPropertyName("tools")]
    public IEnumerable<object> Tools { get; set; } = [];

    [JsonPropertyName("reasoning")]
    public object? Reasoning { get; set; }

    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("error")]
    public ResponseResultError? Error { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }
}

public class ResponseResultError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
