using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// A Responses tool-search request. Server execution has a null call_id; client
/// execution has a call_id that must be echoed by the corresponding output.
/// </summary>
public sealed class ResponseToolSearchCallItem : ResponseInputItem
{
    public ResponseToolSearchCallItem() => Type = "tool_search_call";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("execution")]
    public string Execution { get; set; } = "server";

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; } = JsonSerializer.SerializeToElement(new { });

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// The dynamically loaded tool set produced by hosted or client tool search.
/// </summary>
public sealed class ResponseToolSearchOutputItem : ResponseInputItem
{
    public ResponseToolSearchOutputItem() => Type = "tool_search_output";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("execution")]
    public string Execution { get; set; } = "server";

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("tools")]
    public List<ResponseToolDefinition> Tools { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Adds tools at an exact position in Responses input history.
/// </summary>
public sealed class ResponseAdditionalToolsItem : ResponseInputItem
{
    public ResponseAdditionalToolsItem() => Type = "additional_tools";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "developer";

    [JsonPropertyName("tools")]
    public List<ResponseToolDefinition> Tools { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
