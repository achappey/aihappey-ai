using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// Identifies the OpenAI Responses agent that owns an item or stream event.
/// </summary>
public sealed class ResponseAgent
{
    [JsonPropertyName("agent_name")]
    public string AgentName { get; set; } = default!;
}

/// <summary>
/// A provider-executed OpenAI Responses multi-agent action.
/// </summary>
public sealed class ResponseMultiAgentCallItem : ResponseInputItem
{
    public ResponseMultiAgentCallItem()
    {
        Type = "multi_agent_call";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = default!;

    [JsonPropertyName("agent")]
    public ResponseAgent? Agent { get; set; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;
}

/// <summary>
/// The provider-executed result paired with a multi-agent action.
/// </summary>
public sealed class ResponseMultiAgentCallOutputItem : ResponseInputItem
{
    public ResponseMultiAgentCallOutputItem()
    {
        Type = "multi_agent_call_output";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = default!;

    [JsonPropertyName("agent")]
    public ResponseAgent? Agent { get; set; }

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;

    [JsonPropertyName("output")]
    public JsonElement Output { get; set; } = JsonSerializer.SerializeToElement(Array.Empty<object>());
}

/// <summary>
/// Opaque inter-agent traffic. This item is retained only for native replay and
/// must not be surfaced as unified text, reasoning, tool, or data UI content.
/// </summary>
public sealed class ResponseAgentMessageItem : ResponseInputItem
{
    public ResponseAgentMessageItem()
    {
        Type = "agent_message";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("agent")]
    public ResponseAgent? Agent { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; } = JsonSerializer.SerializeToElement(Array.Empty<object>());

    [JsonPropertyName("recipient")]
    public string? Recipient { get; set; }
}
