using System.Text.Json.Serialization;

namespace AIHappey.Unified.Models;

public sealed class AITextStartEventData
{
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}

public sealed class AITextDeltaEventData
{
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}

public sealed class AITextEndEventData
{
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, object>? ProviderMetadata { get; init; }
}

public sealed class AIReasoningStartEventData
{
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
}

public sealed class AIReasoningDeltaEventData
{
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
}

public sealed class AIReasoningEndEventData
{
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
}

public sealed class AIToolApprovalRequestEventData
{
    [JsonPropertyName("approvalId")]
    public required string ApprovalId { get; init; }

    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }
}

public sealed class AIToolInputStartEventData
{
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    [JsonPropertyName("providerExecuted")]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

public sealed class AIToolInputDeltaEventData
{
    [JsonPropertyName("inputTextDelta")]
    public required string InputTextDelta { get; init; }
}

public sealed class AIToolInputAvailableEventData
{
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    [JsonPropertyName("input")]
    public required object Input { get; init; }

    [JsonPropertyName("providerExecuted")]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
}

public sealed class AIToolOutputAvailableEventData
{
    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("output")]
    public required object Output { get; init; }

    [JsonPropertyName("providerExecuted")]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("dynamic")]
    public bool? Dynamic { get; init; }

    [JsonPropertyName("preliminary")]
    public bool? Preliminary { get; init; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
}

public sealed class AIToolOutputErrorEventData
{
    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("errorText")]
    public required string ErrorText { get; init; }

    [JsonPropertyName("providerExecuted")]
    public bool? ProviderExecuted { get; init; }

    [JsonPropertyName("dynamic")]
    public bool? Dynamic { get; init; }
}

public sealed class AISourceUrlEventData
{
    [JsonPropertyName("sourceId")]
    public required string SourceId { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("container_id")]
    public string? ContainerId { get; init; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; init; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
}

public sealed class AIFinishEventData
{
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("messageMetadata")]
    public Dictionary<string, object>? MessageMetadata { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("completed_at")]
    public object? CompletedAt { get; init; }

    [JsonPropertyName("inputTokens")]
    public int? InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public int? OutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public int? TotalTokens { get; init; }

    [JsonPropertyName("sequence_number")]
    public int? SequenceNumber { get; init; }

    [JsonPropertyName("response")]
    public object? Response { get; init; }

    [JsonPropertyName("stopSequence")]
    public string? StopSequence { get; init; }
}

public sealed class AIErrorEventData
{
    [JsonPropertyName("errorText")]
    public required string ErrorText { get; init; }
}

public sealed class AIAbortEventData
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class AIDataEventData
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("data")]
    public required object Data { get; init; }

    [JsonPropertyName("transient")]
    public bool? Transient { get; init; }
}
