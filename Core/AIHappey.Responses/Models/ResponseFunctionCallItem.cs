using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// Input item for a prior tool/function call in the /v1/responses conversation graph.
/// </summary>
public sealed class ResponseFunctionCallItem : ResponseInputItem
{
    public ResponseFunctionCallItem()
    {
        Type = "function_call";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("caller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseCaller? Caller { get; set; }
}

public sealed class ResponseImageGenerationCallItem : ResponseInputItem
{
    public ResponseImageGenerationCallItem()
    {
        Type = "image_generation_call";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; } = null!;

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("caller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseCaller? Caller { get; set; }
}

/// <summary>
/// Identifies the provider item that invoked a nested response tool call.
/// </summary>
public sealed class ResponseCaller
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("caller_id")]
    public string CallerId { get; set; } = default!;
}

/// <summary>
/// Input item for replaying a prior programmatic tool call.
/// </summary>
public sealed class ResponseProgramItem : ResponseInputItem
{
    public ResponseProgramItem()
    {
        Type = "program";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = default!;
}

/// <summary>
/// Input item for replaying the result of a prior programmatic tool call.
/// </summary>
public sealed class ResponseProgramOutputItem : ResponseInputItem
{
    public ResponseProgramOutputItem()
    {
        Type = "program_output";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

/// <summary>
/// Input item for a prior tool/function call output in the /v1/responses conversation graph.
/// </summary>
public sealed class ResponseFunctionCallOutputItem : ResponseInputItem
{
    public ResponseFunctionCallOutputItem()
    {
        Type = "function_call_output";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;

    [JsonPropertyName("output")]
    public string Output { get; set; } = "{}";

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

/// <summary>
/// Input item for replaying prior reasoning summaries to a /v1/responses backend.
/// </summary>
public sealed class ResponseReasoningItem : ResponseInputItem
{
    public ResponseReasoningItem()
    {
        Type = "reasoning";
    }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("summary")]
    public List<ResponseReasoningSummaryTextPart> Summary { get; set; } = [];

    [JsonPropertyName("encrypted_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedContent { get; set; }

   /* [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }*/
}

public sealed class ResponseReasoningSummaryTextPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "summary_text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = default!;
}
