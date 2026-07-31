
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

public sealed class ResponseInputItemJsonConverter : JsonConverter<ResponseInputItem>
{
    public override ResponseInputItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // "type" is optional in some docs/implementations, but usually present.
        var type = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;

        // Fallback heuristic: if it has "role" -> message
        if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) ||
            (type is null && root.TryGetProperty("role", out _)))
        {
            return root.Deserialize<ResponseInputMessage>(options)
                   ?? throw new JsonException("Could not deserialize message input item.");
        }

        if (string.Equals(type, "item_reference", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseItemReference>(options)
                   ?? throw new JsonException("Could not deserialize item_reference.");
        }

        if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseFunctionCallItem>(options)
                   ?? throw new JsonException("Could not deserialize function_call.");
        }

        if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseFunctionCallOutputItem>(options)
                   ?? throw new JsonException("Could not deserialize function_call_output.");
        }

        if (string.Equals(type, "web_search_call", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseWebSearchCallItem>(options)
                   ?? throw new JsonException("Could not deserialize web_search_call.");
        }

        if (string.Equals(type, "tool_search_call", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseToolSearchCallItem>(options)
                   ?? throw new JsonException("Could not deserialize tool_search_call.");
        }

        if (string.Equals(type, "tool_search_output", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseToolSearchOutputItem>(options)
                   ?? throw new JsonException("Could not deserialize tool_search_output.");
        }

        if (string.Equals(type, "additional_tools", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseAdditionalToolsItem>(options)
                   ?? throw new JsonException("Could not deserialize additional_tools.");
        }

        if (string.Equals(type, "program", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseProgramItem>(options)
                   ?? throw new JsonException("Could not deserialize program.");
        }

        if (string.Equals(type, "program_output", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseProgramOutputItem>(options)
                   ?? throw new JsonException("Could not deserialize program_output.");
        }

        if (string.Equals(type, "code_interpreter_call", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseCodeInterpreterCallItem>(options)
                   ?? throw new JsonException("Could not deserialize code_interpreter_call.");
        }

        if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseReasoningItem>(options)
                   ?? throw new JsonException("Could not deserialize reasoning.");
        }

        if (string.Equals(type, "compaction", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseCompactionItem>(options)
                   ?? throw new JsonException("Could not deserialize compaction.");
        }

        throw new JsonException($"Unknown input item type: '{type ?? "(missing)"}'");
    }

    public override void Write(Utf8JsonWriter writer, ResponseInputItem value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ResponseInputMessage msg:
                JsonSerializer.Serialize(writer, msg, options);
                return;

            case ResponseItemReference itemRef:
                JsonSerializer.Serialize(writer, itemRef, options);
                return;

            case ResponseFunctionCallItem functionCall:
                JsonSerializer.Serialize(writer, functionCall, options);
                return;

            case ResponseFunctionCallOutputItem functionCallOutput:
                JsonSerializer.Serialize(writer, functionCallOutput, options);
                return;

            case ResponseWebSearchCallItem webSearchCall:
                JsonSerializer.Serialize(writer, webSearchCall, options);
                return;

            case ResponseToolSearchCallItem toolSearchCall:
                JsonSerializer.Serialize(writer, toolSearchCall, options);
                return;

            case ResponseToolSearchOutputItem toolSearchOutput:
                JsonSerializer.Serialize(writer, toolSearchOutput, options);
                return;

            case ResponseAdditionalToolsItem additionalTools:
                JsonSerializer.Serialize(writer, additionalTools, options);
                return;

            case ResponseProgramItem program:
                JsonSerializer.Serialize(writer, program, options);
                return;

            case ResponseProgramOutputItem programOutput:
                JsonSerializer.Serialize(writer, programOutput, options);
                return;

            case ResponseCodeInterpreterCallItem codeInterpreterCall:
                JsonSerializer.Serialize(writer, codeInterpreterCall, options);
                return;

            case ResponseReasoningItem reasoning:
                JsonSerializer.Serialize(writer, reasoning, options);
                return;

            case ResponseCompactionItem compaction:
                JsonSerializer.Serialize(writer, compaction, options);
                return;

            default:
                throw new JsonException($"Unsupported input item: {value.GetType().Name}");
        }
    }
}
