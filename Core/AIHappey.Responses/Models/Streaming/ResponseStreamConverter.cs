using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses.Streaming;

public class ResponseStreamConverter : JsonConverter<ResponseStreamPart>
{
    private static readonly Dictionary<string, Type> PartTypeMap = new()
    {
        ["response.created"] = typeof(ResponseCreated),
        ["response.completed"] = typeof(ResponseCompleted),
        ["response.failed"] = typeof(ResponseFailed),
        ["response.in_progress"] = typeof(ResponseInProgress),
        ["response.output_item.added"] = typeof(ResponseOutputItemAdded),
        ["response.output_item.done"] = typeof(ResponseOutputItemDone),
        ["response.content_part.added"] = typeof(ResponseContentPartAdded),
        ["response.content_part.done"] = typeof(ResponseContentPartDone),
        ["response.output_text.delta"] = typeof(ResponseOutputTextDelta),
        ["response.output_text.done"] = typeof(ResponseOutputTextDone),
        ["response.output_text.annotation.added"] = typeof(ResponseOutputTextAnnotationAdded),
        ["response.function_call_arguments.delta"] = typeof(ResponseFunctionCallArgumentsDelta),
        ["response.function_call_arguments.done"] = typeof(ResponseFunctionCallArgumentsDone),
        ["response.mcp_call_arguments.delta"] = typeof(ResponseMcpCallArgumentsDelta),
        ["response.mcp_call_arguments.done"] = typeof(ResponseMcpCallArgumentsDone),
        ["response.reasoning_summary_part.added"] = typeof(ResponseReasoningSummaryPartAdded),
        ["response.reasoning_summary_part.done"] = typeof(ResponseReasoningSummaryPartDone),
        ["response.reasoning_summary_text.delta"] = typeof(ResponseReasoningSummaryTextDelta),
        ["response.reasoning_summary_text.done"] = typeof(ResponseReasoningSummaryTextDone),
        ["response.reasoning_text.delta"] = typeof(ResponseReasoningTextDelta),
        ["response.reasoning_text.done"] = typeof(ResponseReasoningTextDone),
        ["response.refusal.delta"] = typeof(ResponseRefusalDelta),
        ["response.refusal.done"] = typeof(ResponseRefusalDone),
        ["response.file_search_call.completed"] = typeof(ResponseFileSearchCallCompleted),
        ["response.file_search_call.in_progress"] = typeof(ResponseFileSearchCallInProgress),
        ["response.file_search_call.searching"] = typeof(ResponseFileSearchCallSearching),
        ["response.web_search_call.completed"] = typeof(ResponseWebSearchCallCompleted),
        ["response.web_search_call.in_progress"] = typeof(ResponseWebSearchCallInProgress),
        ["response.web_search_call.searching"] = typeof(ResponseWebSearchCallSearching),
        ["error"] = typeof(ResponseError),
    };

    public static ResponseStreamPart DeserializePart(string typeProp, JsonElement root, JsonSerializerOptions options)
    {
        if (PartTypeMap.TryGetValue(typeProp, out var targetType))
        {
            var part = JsonSerializer.Deserialize(root.GetRawText(), targetType, options)
                       ?? throw new JsonException($"Failed to deserialize type: {typeProp}");

            // Cast to UIMessagePart, will throw if not correct type
            return part as ResponseStreamPart
                   ?? throw new JsonException($"Deserialized type is not a ResponseStreamPart: {typeProp}");
        }

        var unknownEvent = JsonSerializer.Deserialize<ResponseUnknownEvent>(root.GetRawText(), options);

        return unknownEvent is not null
            ? unknownEvent
            : throw new JsonException($"Unknown ResponseStreamPart type discriminator: {typeProp}");
    }

    public override ResponseStreamPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string typeProp = root.GetProperty("type").GetString() ?? throw new ArgumentException();

        return DeserializePart(typeProp!, root, options);
    }

    public override void Write(Utf8JsonWriter writer, ResponseStreamPart value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
