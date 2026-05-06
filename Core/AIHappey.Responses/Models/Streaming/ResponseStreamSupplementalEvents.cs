using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses.Streaming;

public class ResponseStreamItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = default!;

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("phase")]
    public string? Phase { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    [JsonPropertyName("max_output_length")]
    public int? MaxOutputLength { get; init; }

    [JsonPropertyName("content")]
    //public IReadOnlyList<ResponseStreamContentPart>? Content { get; init; }
    public JsonElement? Content { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public class ResponseStreamContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = default!;

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("annotations")]
    public IReadOnlyList<ResponseStreamAnnotation>? Annotations { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public class ResponseStreamAnnotation
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public abstract class ResponseStreamIndexedPart : ResponseStreamPart
{
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
}

public abstract class ResponseStreamItemEvent : ResponseStreamIndexedPart
{
    [JsonPropertyName("item")]
    public ResponseStreamItem Item { get; init; } = default!;
}

public sealed class ResponseOutputItemAdded : ResponseStreamItemEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.output_item.added";
}

public sealed class ResponseOutputItemDone : ResponseStreamItemEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.output_item.done";
}

public abstract class ResponseStreamItemContentEvent : ResponseStreamIndexedPart
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = default!;

    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }
}

public sealed class ResponseContentPartAdded : ResponseStreamItemContentEvent
{
    [JsonPropertyName("part")]
    public ResponseStreamContentPart Part { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.content_part.added";
}

public sealed class ResponseContentPartDone : ResponseStreamItemContentEvent
{
    [JsonPropertyName("part")]
    public ResponseStreamContentPart Part { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.content_part.done";
}

public sealed class ResponseOutputTextAnnotationAdded : ResponseStreamItemContentEvent
{
    [JsonPropertyName("annotation_index")]
    public int AnnotationIndex { get; init; }

    [JsonPropertyName("annotation")]
    public ResponseStreamAnnotation Annotation { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.output_text.annotation.added";
}

public abstract class ResponseStreamTextEvent : ResponseStreamItemContentEvent
{
}


public sealed class ResponseImageGenerationCallPartialImage : ResponseStreamTextEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.image_generation_call.partial_image";

    [JsonPropertyName("partial_image_b64")]
    public string PartialImageB64 { get; init; } = default!;

    [JsonPropertyName("output_format")]
    public string OutputFormat { get; init; } = default!;
}


public sealed class ResponseImageGenerationCallGenerating : ResponseStreamTextEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.image_generation_call.generating";
}

public sealed class ResponseImageGenerationCallInProgress : ResponseStreamTextEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.image_generation_call.in_progress";
}

public sealed class ResponseReasoningSummaryTextDelta : ResponseStreamTextEvent
{
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_summary_text.delta";

    [JsonPropertyName("summary_index")]
    public int SummaryIndex { get; init; } = default!;
}

public sealed class ResponseReasoningSummaryTextDone : ResponseStreamTextEvent
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_summary_text.done";

    [JsonPropertyName("summary_index")]
    public int SummaryIndex { get; init; } = default!;
}

public sealed class ResponseReasoningTextDelta : ResponseStreamTextEvent
{
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_text.delta";
}

public sealed class ResponseReasoningTextDone : ResponseStreamTextEvent
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_text.done";
}

public sealed class ResponseRefusalDelta : ResponseStreamTextEvent
{
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.refusal.delta";
}

public sealed class ResponseRefusalDone : ResponseStreamTextEvent
{
    [JsonPropertyName("refusal")]
    public string Refusal { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.refusal.done";
}

public sealed class ResponseReasoningSummaryPartAdded : ResponseStreamItemContentEvent
{
    [JsonPropertyName("part")]
    public ResponseStreamContentPart Part { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_summary_part.added";

    [JsonPropertyName("summary_index")]
    public int SummaryIndex { get; init; } = default!;
}

public sealed class ResponseReasoningSummaryPartDone : ResponseStreamItemContentEvent
{
    [JsonPropertyName("part")]
    public ResponseStreamContentPart Part { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_summary_part.done";

    [JsonPropertyName("summary_index")]
    public int SummaryIndex { get; init; } = default!;
}

public sealed class ResponseReasoningPartAdded : ResponseStreamItemContentEvent
{
    [JsonPropertyName("part")]
    public ResponseStreamContentPart Part { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_part.added";
}

public sealed class ResponseReasoningPartDone : ResponseStreamItemContentEvent
{
    [JsonPropertyName("part")]
    public ResponseStreamContentPart Part { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.reasoning_part.done";
}

public abstract class ResponseToolCallArgumentsEvent : ResponseStreamIndexedPart
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = default!;
}

public sealed class ResponseFunctionCallArgumentsDelta : ResponseToolCallArgumentsEvent
{
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.function_call_arguments.delta";
}

public sealed class ResponseFunctionCallArgumentsDone : ResponseToolCallArgumentsEvent
{
    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.function_call_arguments.done";
}

public sealed class ResponseMcpCallArgumentsDelta : ResponseToolCallArgumentsEvent
{
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.mcp_call_arguments.delta";
}

public sealed class ResponseMcpCallArgumentsDone : ResponseToolCallArgumentsEvent
{
    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.mcp_call_arguments.done";
}

public abstract class ResponseToolCallStatusEvent : ResponseStreamIndexedPart
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = default!;
}

public sealed class ResponseCodeInterpreterCallInProgress : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.code_interpreter_call.in_progress";
}


public sealed class ResponseCodeInterpreterCallDone : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.code_interpreter_call_code.done";

    [JsonPropertyName("code")]
    public string Code { get; init; } = default!;
}

public sealed class ResponseCodeInterpreterCallCodeDelta : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.code_interpreter_call_code.delta";

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;
}


public sealed class ResponseCustomToolCallInputDelta : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.custom_tool_call_input.delta";

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;
}


public sealed class ResponseCustomToolCallInputDone : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.custom_tool_call_input.done";

    [JsonPropertyName("input")]
    public string Input { get; init; } = default!;
}


public abstract class ResponseShellCallCommandEvent : ResponseStreamIndexedPart
{
    [JsonPropertyName("command_index")]
    public int CommandIndex { get; init; }
}

public sealed class ResponseShellCallCommandAdded : ResponseShellCallCommandEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.shell_call_command.added";

    [JsonPropertyName("command")]
    public string Command { get; init; } = default!;
}

public sealed class ResponseShellCallCommandDelta : ResponseShellCallCommandEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.shell_call_command.delta";

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;
}

public sealed class ResponseShellCallCommandDone : ResponseShellCallCommandEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.shell_call_command.done";

    [JsonPropertyName("command")]
    public string Command { get; init; } = default!;
}

public sealed class ResponseFileSearchCallCompleted : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.file_search_call.completed";
}

public sealed class ResponseFileSearchCallInProgress : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.file_search_call.in_progress";
}

public sealed class ResponseFileSearchCallSearching : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.file_search_call.searching";
}

public sealed class ResponseWebSearchCallCompleted : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.web_search_call.completed";
}

public sealed class ResponseWebSearchCallInProgress : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.web_search_call.in_progress";
}

public sealed class ResponseWebSearchCallSearching : ResponseToolCallStatusEvent
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.web_search_call.searching";
}

public sealed class ResponseUnknownEvent : ResponseStreamPart
{
    [JsonPropertyName("sequence_number")]
    public int? SequenceNumber { get; init; }

    [JsonPropertyName("type")]
    public override string Type { get; init; } = default!;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Data { get; init; }
}
