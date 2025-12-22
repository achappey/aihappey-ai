using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.xAI.Models;

public class XAIChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "grok-4-fast-non-reasoning";

    [JsonPropertyName("messages")]
    public IEnumerable<XAIMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

}

public class XAIMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public IEnumerable<IXAIMessageContent> Content { get; set; } = [];

}

public class XAIFunctionCall : IXAIMessageContent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

}

public class XAIFunctionCallOutput : IXAIMessageContent
{
    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = null!;

    [JsonPropertyName("output")]
    public string? Output { get; set; }

}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(XAIMessageContent), "input_text")]
[JsonDerivedType(typeof(XAIImageUrlContent), "input_image")]
[JsonDerivedType(typeof(XAIFunctionCall), "function_call")]
[JsonDerivedType(typeof(XAIFunctionCallOutput), "function_call_output")]
public abstract class IXAIMessageContent
{
}

public class XAIMessageContent : IXAIMessageContent
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class XAIImageUrlContent : IXAIMessageContent
{
    [JsonPropertyName("image_url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "auto";
}

