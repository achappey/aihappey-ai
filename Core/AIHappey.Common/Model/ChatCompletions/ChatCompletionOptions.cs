
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.ChatCompletions;

public class ChatCompletionOptions
{

    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("messages")]
    public IEnumerable<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public IEnumerable<object> Tools { get; set; } = [];

    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ResponseFormat { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = default!;

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<object>? ToolCalls { get; set; }
}