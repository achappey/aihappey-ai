
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AIHappey.Common.Model.Responses;

public sealed class ResponseRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    /// <summary>
    /// ✅ input: string OR array
    /// </summary>
    [JsonPropertyName("input")]
    public ResponseInput? Input { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; } // ✅ number

    [JsonPropertyName("truncation")]
    public TruncationStrategy? Truncation { get; set; } // ✅ "auto" | "disabled"

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("top_logprobs")]
    public int? TopLogprobs { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("include")]
    public List<string>? Include { get; set; }

    /// <summary>
    /// Docs say: "map of up to 16 keys; values are string/boolean/number".
    /// Keep as object? for max flexibility.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// Tools are a big tree. Keep as flexible objects for now.
    /// If you want, I can type all tool variants too.
    /// </summary>
    [JsonPropertyName("tools")]
    public List<ResponseToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; } // string or object (keep flexible in headstart)
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TruncationStrategy
{
    [JsonPropertyName("auto")]
    Auto,

    [JsonPropertyName("disabled")]
    Disabled
}

#region ResponseInput (string OR array)



#endregion

#region Input Items (message, item_reference)

/// <summary>
/// ✅ Item in input array. Discriminator: "type"
/// Commonly: { type:"message", role:"user", content:[...] }
/// </summary>
[JsonConverter(typeof(ResponseInputItemJsonConverter))]
public abstract class ResponseInputItem
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResponseRole
{
    User,
    Assistant,
    System,
    Developer
}

#endregion

#region Content Parts (input_text, input_image, input_file)

[JsonConverter(typeof(ResponseContentPartJsonConverter))]
public abstract class ResponseContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;
}

public sealed class InputTextPart : ResponseContentPart
{
    public InputTextPart()
    {
        Type = "input_text";
    }

    public InputTextPart(string text) : this()
    {
        Text = text;
    }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public sealed class InputImagePart : ResponseContentPart
{
    public InputImagePart()
    {
        Type = "input_image";
    }

    /// <summary>
    /// "high" | "low" | "auto"
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    /// <summary>
    /// URL or data URL (base64)
    /// </summary>
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }
}

#endregion

public static class ResponseJson
{
    /// <summary>
    /// Plug this into your HttpClient JSON calls.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new ResponseInputJsonConverter(),
            new ResponseInputItemJsonConverter(),
            new ResponseMessageContentJsonConverter(),
            new ResponseContentPartJsonConverter(),
        }
    };
}
