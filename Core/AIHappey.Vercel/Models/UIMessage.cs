using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Role
{
    system,
    user,
    assistant
}

public class UIMessage
{
    public string Id { get; init; } = default!;

    public Role Role { get; init; }

    public List<UIMessagePart> Parts { get; set; } = [];

    public Dictionary<string, object>? Metadata { get; init; }
}

public class CreateUIMessage : UIMessage
{
    public new string? Id { get; init; }
}

[JsonConverter(typeof(UIMessagePartConverter))]
//[JsonDerivedType(typeof(DataUIPart<JsonElement>), "data-table")] // Add more if needed
public abstract class UIMessagePart
{
    public abstract string Type { get; init; }
}

public class TextUIPart : UIMessagePart
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "text";

}

public class TextStartUIMessageStreamPart : UIMessagePart
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "text-start";

}

public class TextEndUIMessageStreamPart : UIMessagePart
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "text-end";

}

public class TextDeltaUIMessageStreamPart : UIMessagePart
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "text-delta";

}

public class ReasoningUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "reasoning";

    [JsonPropertyName("text")]
    public string Text { get; init; } = default!;

    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}



public class ReasoningStartUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "reasoning-start";

    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}

public class ReasoningDeltaUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "reasoning-delta";

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}

public class ReasoningEndUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "reasoning-end";

    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}


public class ToolApprovalRequestPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "tool-approval-request";

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

    [JsonPropertyName("approvalId")]
    public string ApprovalId { get; init; } = default!;
}


public class ToolCallPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "tool-input-available";

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = default!;

    [JsonPropertyName("input")]
    public object Input { get; init; } = default!;

    [JsonPropertyName("providerExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    public static ToolCallPart CreateProviderExecuted(string id, string toolName, object input) =>
        new()
        {
            ProviderExecuted = true,
            ToolCallId = id,
            ToolName = toolName,
            Input = input,
        };
}

public class ToolCallStreamingStartPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "tool-input-start";

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = default!;

    [JsonPropertyName("providerExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    public static ToolCallStreamingStartPart CreateProviderExecuted(string id, string toolName) =>
        new()
        {
            ProviderExecuted = true,
            ToolCallId = id,
            ToolName = toolName
        };

}


public class ToolCallDeltaPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "tool-input-delta";

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

    [JsonPropertyName("inputTextDelta")]
    public string InputTextDelta { get; init; } = default!;
}

public class ToolOutputAvailablePart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "tool-output-available";

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

    [JsonPropertyName("output")]
    public object Output { get; init; } = default!;

    [JsonPropertyName("providerExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("dynamic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Dynamic { get; init; }

    [JsonPropertyName("preliminary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Preliminary { get; init; }

}

public class ToolOutputErrorPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "tool-output-error";

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

    [JsonPropertyName("errorText")]
    public string ErrorText { get; init; } = default!;

    [JsonPropertyName("providerExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("dynamic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Dynamic { get; init; }
}

public class ToolApproval
{
    [JsonPropertyName("approved")]
    public bool? Approved { get; init; }

    [JsonPropertyName("Id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public class ToolInvocationPart : UIMessagePart
{
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("input")]
    public object Input { get; init; } = default!;

    // Add other properties as sent by the client
    [JsonPropertyName("state")]
    public string State { get; init; } = default!;

    [JsonPropertyName("output")]
    public object Output { get; set; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = default!;

    [JsonPropertyName("providerExecuted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("approval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolApproval? Approval { get; init; }

}

public class SourceUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "source-url";

    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = default!;

    [JsonPropertyName("url")]
    public string Url { get; init; } = default!;

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}

public class SourceDocumentPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "source-document";

    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = default!;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = default!;

    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = default!;

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}


public class FileUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "file";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = default!;

    [JsonPropertyName("url")]
    public string Url { get; init; } = default!;

    //   [JsonPropertyName("providerMetadata")]
    //  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    // public Dictionary<string, Dictionary<string, object>?>? ProviderMetadata { get; init; }
}


public class MessageMetadataUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "message-metadata";

    [JsonPropertyName("messageMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? MessageMetadata { get; init; }
}

public class FinishUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "finish";

    [JsonPropertyName("messageMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? MessageMetadata { get; init; }

    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; init; }

}

public class StepStartUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "step-start";
}

public class StartStepUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "start-step";

}

public class ErrorUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "error";

    [JsonPropertyName("errorText")]
    public string ErrorText { get; init; } = default!;
}

public class DataUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = default!;

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("data")]
    public object Data { get; init; } = default!;

    [JsonPropertyName("transient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Transient { get; init; }
}

public class AbortUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "abort";

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

}


public class ToolApprovalRequestUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "tool-approval-request";

    [JsonPropertyName("approvalId")]
    public string ApprovalId { get; init; } = default!;

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; init; } = default!;

}


public class UIMessagePartConverter : JsonConverter<UIMessagePart>
{
    private static readonly Dictionary<string, Type> PartTypeMap = new()
    {
        ["text"] = typeof(TextUIPart),
        ["text-start"] = typeof(TextStartUIMessageStreamPart),
        ["text-delta"] = typeof(TextDeltaUIMessageStreamPart),
        ["text-end"] = typeof(TextEndUIMessageStreamPart),
        ["reasoning"] = typeof(ReasoningUIPart),
        ["reasoning-start"] = typeof(ReasoningStartUIPart),
        ["reasoning-delta"] = typeof(ReasoningDeltaUIPart),
        ["reasoning-end"] = typeof(ReasoningEndUIPart),
        ["abort"] = typeof(AbortUIPart),
        ["tool-call"] = typeof(ToolCallPart),
        ["tool-input-start"] = typeof(ToolCallStreamingStartPart),
        ["tool-input-delta"] = typeof(ToolCallDeltaPart),
        ["message-metadata"] = typeof(MessageMetadataUIPart),
        ["tool-input-available"] = typeof(ToolCallPart),
        ["tool-output-available"] = typeof(ToolOutputAvailablePart),
        ["tool-output-error"] = typeof(ToolOutputErrorPart),
        ["source-url"] = typeof(SourceUIPart),
        ["tool-approval-request"] = typeof(ToolApprovalRequestUIPart),
        ["source-document"] = typeof(SourceDocumentPart),
        ["file"] = typeof(FileUIPart),
        ["error"] = typeof(ErrorUIPart),
        ["step-start"] = typeof(StepStartUIPart),
        ["start-step"] = typeof(StartStepUIPart),
        ["finish"] = typeof(FinishUIPart)
    };

    public static UIMessagePart DeserializePart(string typeProp, JsonElement root, JsonSerializerOptions options)
    {
        if (PartTypeMap.TryGetValue(typeProp, out var targetType))
        {
            var part = JsonSerializer.Deserialize(root.GetRawText(), targetType, options)
                       ?? throw new JsonException($"Failed to deserialize type: {typeProp}");

            // Cast to UIMessagePart, will throw if not correct type
            return part as UIMessagePart
                   ?? throw new JsonException($"Deserialized type is not a UIMessagePart: {typeProp}");
        }

        throw new JsonException($"Unknown UIMessagePart type discriminator: {typeProp}");
    }

    public override UIMessagePart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string typeProp = root.GetProperty("type").GetString() ?? throw new ArgumentException();

        if (typeProp != null
            && typeProp.StartsWith("tool-")
            && !typeProp.StartsWith("tool-input")
            && !typeProp.StartsWith("tool-output")
            && typeProp != "tool-call")
        {
            return JsonSerializer.Deserialize<ToolInvocationPart>(root.GetRawText(), options);
        }

        if (typeProp != null
           && typeProp.StartsWith("data-"))
        {
            return JsonSerializer.Deserialize<DataUIPart>(root.GetRawText(), options);
        }

        return DeserializePart(typeProp!, root, options);
        /*
                // Handle all fixed types (exact match)
                return typeProp switch
                {
                    "text" => JsonSerializer.Deserialize<TextUIPart>(root.GetRawText(), options),
                    "text-start" => JsonSerializer.Deserialize<TextStartUIMessageStreamPart>(root.GetRawText(), options),
                    "text-delta" => JsonSerializer.Deserialize<TextDeltaUIMessageStreamPart>(root.GetRawText(), options),
                    "text-end" => JsonSerializer.Deserialize<TextEndUIMessageStreamPart>(root.GetRawText(), options),
                    "reasoning" => JsonSerializer.Deserialize<ReasoningUIPart>(root.GetRawText(), options),
                    "tool-call" => JsonSerializer.Deserialize<ToolCallPart>(root.GetRawText(), options),
                    "tool-input-start" => JsonSerializer.Deserialize<ToolCallStreamingStartPart>(root.GetRawText(), options),
                    "tool-input-delta" => JsonSerializer.Deserialize<ToolCallDeltaPart>(root.GetRawText(), options),
                    "tool-input-available" => JsonSerializer.Deserialize<ToolCallPart>(root.GetRawText(), options),
                    "source-url" => JsonSerializer.Deserialize<SourceUIPart>(root.GetRawText(), options),
                    "source-document" => JsonSerializer.Deserialize<SourceDocumentPart>(root.GetRawText(), options),
                    "file" => JsonSerializer.Deserialize<FileUIPart>(root.GetRawText(), options),
                    "step-start" => JsonSerializer.Deserialize<StepStartUIPart>(root.GetRawText(), options),
                    "start-step" => JsonSerializer.Deserialize<StartStepUIPart>(root.GetRawText(), options),
                    "finish" => JsonSerializer.Deserialize<FinishUIPart>(root.GetRawText(), options),
                    _ => throw new JsonException($"Unknown UIMessagePart type discriminator: {typeProp}")
                };
        */
        // throw new JsonException($"Unknown type: {typeProp}");
    }

    public override void Write(Utf8JsonWriter writer, UIMessagePart value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }
}