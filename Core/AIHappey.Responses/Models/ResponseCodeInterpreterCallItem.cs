using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// A completed provider-executed Code Interpreter item replayed as native
/// Responses API state. Unlike client function tools, this is a single item
/// that contains both the executed code and its outputs.
/// </summary>
public sealed class ResponseCodeInterpreterCallItem : ResponseInputItem
{
    public ResponseCodeInterpreterCallItem()
    {
        Type = "code_interpreter_call";
    }

    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("container_id")]
    public string ContainerId { get; set; } = default!;

    [JsonPropertyName("outputs")]
    public JsonElement Outputs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "completed";

    [JsonPropertyName("caller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseCaller? Caller { get; set; }
}
