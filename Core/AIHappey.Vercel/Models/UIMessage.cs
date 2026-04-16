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

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}

public class TextEndUIMessageStreamPart : UIMessagePart
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "text-end";

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; init; }

}

public class TextDeltaUIMessageStreamPart : UIMessagePart
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "text-delta";

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; init; }

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
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
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
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
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

    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; init; }

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]

    public Dictionary<string, Dictionary<string, object>?>? ProviderMetadata { get; init; }
}


public class MessageMetadataUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "message-metadata";

    [JsonPropertyName("messageMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? MessageMetadata { get; init; }
}

public sealed record FinishGatewayMetadata
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    [JsonPropertyName("cost")]
    public decimal? Cost { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public Dictionary<string, object?> ToDictionary()
        => JsonSerializer.SerializeToElement(this, Json)
            .Deserialize<Dictionary<string, object?>>(Json)
            ?? [];

    public static FinishGatewayMetadata? FromDictionary(Dictionary<string, object>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        return JsonSerializer.SerializeToElement(metadata, Json)
            .Deserialize<FinishGatewayMetadata>(Json);
    }
}


public sealed record Usage
{
    [JsonPropertyName("promptTokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completionTokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public int? TotalTokens { get; init; }

}

public sealed record FinishMessageMetadata
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly HashSet<string> LegacyMetadataKeysToStrip =
    [
        "inputTokens",
        "outputTokens",
        "totalTokens",
        "reasoningTokens",
        "cachedInputTokens",
        "cachedInputReadTokens",
        "cachedInputWriteTokens",
        "runtimeMs"
    ];

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; init; }

    [JsonPropertyName("gateway")]
    public FinishGatewayMetadata? Gateway { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public Dictionary<string, object?> ToDictionary()
        => JsonSerializer.SerializeToElement(this, Json)
            .Deserialize<Dictionary<string, object?>>(Json)
            ?? [];

    public static FinishMessageMetadata Create(
        string model,
        DateTimeOffset timestamp,
        int? outputTokens = null,
        int? inputTokens = null,
        int? totalTokens = null,
        float? temperature = null,
        int? reasoningTokens = null,
        int? cachedInputTokens = null,
        int? cachedInputReadTokens = null,
        int? cachedInputWriteTokens = null,
        long? runtimeMs = null,
        FinishGatewayMetadata? gateway = null,
        Dictionary<string, object?>? additionalProperties = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["timestamp"] = timestamp,
            ["usage"] = new Usage
            {
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                TotalTokens = totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0))
            },
            ["temperature"] = temperature,
            ["gateway"] = gateway
        };

        if (additionalProperties is not null)
        {
            foreach (var item in additionalProperties)
                metadata[item.Key] = item.Value;
        }

        return JsonSerializer.SerializeToElement(metadata, Json)
            .Deserialize<FinishMessageMetadata>(Json)
            ?? throw new JsonException("Failed to create finish message metadata.");
    }

    public static FinishMessageMetadata FromDictionary(
        Dictionary<string, object>? metadata,
        string? fallbackModel = null,
        DateTimeOffset? fallbackTimestamp = null)
    {
        var merged = metadata?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value) ?? [];

        merged["usage"] = NormalizeUsage(
            merged.TryGetValue("usage", out var usage) ? usage : null,
            ExtractInt(merged, "inputTokens"),
            ExtractInt(merged, "outputTokens"),
            ExtractInt(merged, "totalTokens"));

        foreach (var legacyKey in LegacyMetadataKeysToStrip)
            merged.Remove(legacyKey);

        if (!merged.ContainsKey("model") && !string.IsNullOrWhiteSpace(fallbackModel))
            merged["model"] = fallbackModel;

        if (!merged.ContainsKey("timestamp"))
            merged["timestamp"] = fallbackTimestamp ?? DateTimeOffset.UtcNow;

        return JsonSerializer.SerializeToElement(merged, Json)
            .Deserialize<FinishMessageMetadata>(Json)
            ?? throw new JsonException("Failed to deserialize finish message metadata.");
    }

    public static implicit operator FinishMessageMetadata?(Dictionary<string, object>? metadata)
        => metadata is null ? null : FromDictionary(metadata);

    private static Usage NormalizeUsage(object? usage, int? inputTokens, int? outputTokens, int? totalTokens)
    {
        if (TryReadUsageInt(usage, out var promptTokens, "promptTokens", "prompt_tokens", "inputTokens", "input_tokens")
            || TryReadUsageInt(usage, out _, "completionTokens", "completion_tokens", "outputTokens", "output_tokens")
            || TryReadUsageInt(usage, out _, "totalTokens", "total_tokens"))
        {
            TryReadUsageInt(usage, out var completionTokens, "completionTokens", "completion_tokens", "outputTokens", "output_tokens");
            TryReadUsageInt(usage, out var usageTotalTokens, "totalTokens", "total_tokens");

            return new Usage
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = usageTotalTokens ?? ((promptTokens ?? 0) + (completionTokens ?? 0))
            };
        }

        return new Usage
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0))
        };
    }

    private static int? ExtractInt(Dictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            int value => value,
            long value when value >= int.MinValue && value <= int.MaxValue => (int)value,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var value) => value,
            JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out var value) => value,
            string value when int.TryParse(value, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryReadUsageInt(object? usage, out int? value, params string[] keys)
    {
        value = null;
        if (usage is null)
            return false;

        foreach (var key in keys)
        {
            if (TryReadUsageInt(usage, key, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadUsageInt(object usage, string key, out int value)
    {
        value = 0;

        return usage switch
        {
            Usage normalized => TryReadUsageInt(normalized, key, out value),
            JsonElement json => TryReadUsageInt(json, key, out value),
            Dictionary<string, object?> dictionary => TryReadUsageInt(dictionary, key, out value),
            _ => false
        };
    }

    private static bool TryReadUsageInt(Usage usage, string key, out int value)
    {
        value = key switch
        {
            "promptTokens" or "prompt_tokens" or "inputTokens" or "input_tokens" => usage.PromptTokens ?? 0,
            "completionTokens" or "completion_tokens" or "outputTokens" or "output_tokens" => usage.CompletionTokens ?? 0,
            "totalTokens" or "total_tokens" => usage.TotalTokens ?? 0,
            _ => 0
        };

        return key switch
        {
            "promptTokens" or "prompt_tokens" or "inputTokens" or "input_tokens" => usage.PromptTokens is not null,
            "completionTokens" or "completion_tokens" or "outputTokens" or "output_tokens" => usage.CompletionTokens is not null,
            "totalTokens" or "total_tokens" => usage.TotalTokens is not null,
            _ => false
        };
    }

    private static bool TryReadUsageInt(Dictionary<string, object?> usage, string key, out int value)
    {
        value = 0;
        return usage.TryGetValue(key, out var raw) && TryConvertToInt(raw, out value);
    }

    private static bool TryReadUsageInt(JsonElement usage, string key, out int value)
    {
        value = 0;

        if (usage.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in usage.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                return TryConvertToInt(property.Value, out value);
        }

        return false;
    }

    private static bool TryConvertToInt(object? raw, out int value)
    {
        value = 0;

        switch (raw)
        {
            case int intValue:
                value = intValue;
                return true;
            case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                value = (int)longValue;
                return true;
            case JsonElement json:
                return TryConvertToInt(json, out value);
            case string stringValue when int.TryParse(stringValue, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryConvertToInt(JsonElement raw, out int value)
    {
        value = 0;

        return raw.ValueKind switch
        {
            JsonValueKind.Number when raw.TryGetInt32(out var numericValue) => (value = numericValue) >= 0 || numericValue < 0,
            JsonValueKind.String when int.TryParse(raw.GetString(), out var parsedValue) => (value = parsedValue) >= 0 || parsedValue < 0,
            _ => false
        };
    }

}

public class FinishUIPart : UIMessagePart
{
    [JsonPropertyName("type")]
    public override string Type { get; init; } = "finish";

    [JsonPropertyName("messageMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FinishMessageMetadata? MessageMetadata { get; init; }

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
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
