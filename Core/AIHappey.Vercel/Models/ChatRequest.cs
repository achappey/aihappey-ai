using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

public class ChatRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("messages")]
    public List<UIMessage> Messages { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("toolChoice")]
    public string? ToolChoice { get; set; }

    [JsonPropertyName("maxToolCalls")]
    public int? MaxToolCalls { get; set; }

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 1;

    [JsonPropertyName("topP")]
    public float? TopP { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("tools")]
    public List<Tool>? Tools { get; set; } = [];

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ResponseFormat { get; set; }
   
}

public class ResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "json_schema";

    [JsonPropertyName("json_schema")]
    public JSONSchema JsonSchema { get; set; } = null!;
}

public class JSONSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("schema")]
    public JsonElement Schema { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }


}

