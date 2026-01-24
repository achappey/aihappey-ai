using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Common.Model;

/// <summary>
/// Vercel Model Gateway v3 compatible request DTO for the chat request shape used by the Vercel AI SDK gateway.
/// In this repo it is consumed by <c>POST /api/chat</c> (Vercel AI SDK UI message stream).
/// <para>
/// This JSON shape is a public, contract-locked surface. Do not rename properties, change casing,
/// alter types, or restructure this DTO.
/// </para>
/// <para>
/// Use <see cref="ProviderMetadata"/> for provider-specific options without changing the contract.
/// </para>
/// </summary>
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

