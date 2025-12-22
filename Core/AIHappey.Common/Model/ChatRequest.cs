using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers;

namespace AIHappey.Common.Model;

public class ChatRequest
{
    public string Id { get; set; } = default!;

    public List<UIMessage> Messages { get; set; } = [];

    public string Model { get; set; } = null!;

    public string? ToolChoice { get; set; }

    public float Temperature { get; set; } = 1;

    public float? TopP { get; set; }

    public int? MaxTokens { get; set; }

    public List<Tool>? Tools { get; set; } = [];

    // public ProviderMetadata? ProviderMetadata { get; set; }
  //  public Dictionary<string, object>? ProviderMetadata { get; set; }
    //public JsonElement? ProviderMetadata { get; set; }
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

