using System.Text.Json;
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

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
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

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
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

public sealed class AIFileEventData
{
    [JsonPropertyName("mediaType")]
    public required string MediaType { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; init; }
}

public sealed record AIFinishGatewayMetadata
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

    public static AIFinishGatewayMetadata? FromDictionary(Dictionary<string, object>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        return JsonSerializer.SerializeToElement(metadata, Json)
            .Deserialize<AIFinishGatewayMetadata>(Json);
    }
}

public sealed record AIFinishMessageMetadata
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly JsonElement EmptyUsage = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), Json);

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("usage")]
    public required JsonElement Usage { get; init; }

    [JsonPropertyName("outputTokens")]
    public int? OutputTokens { get; init; }

    [JsonPropertyName("inputTokens")]
    public int? InputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public int? TotalTokens { get; init; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; init; }

    [JsonPropertyName("gateway")]
    public AIFinishGatewayMetadata? Gateway { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public Dictionary<string, object?> ToDictionary()
        => JsonSerializer.SerializeToElement(this, Json)
            .Deserialize<Dictionary<string, object?>>(Json)
            ?? [];

    public static AIFinishMessageMetadata Create(
        string model,
        DateTimeOffset timestamp,
        object? usage = null,
        int? outputTokens = null,
        int? inputTokens = null,
        int? totalTokens = null,
        float? temperature = null,
        int? reasoningTokens = null,
        int? cachedInputTokens = null,
        int? cachedInputReadTokens = null,
        int? cachedInputWriteTokens = null,
        long? runtimeMs = null,
        AIFinishGatewayMetadata? gateway = null,
        Dictionary<string, object?>? additionalProperties = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["timestamp"] = timestamp,
            ["usage"] = NormalizeUsage(usage, inputTokens, outputTokens, totalTokens),
            ["outputTokens"] = outputTokens,
            ["inputTokens"] = inputTokens,
            ["totalTokens"] = totalTokens,
            ["temperature"] = temperature,
            ["reasoningTokens"] = reasoningTokens,
            ["cachedInputTokens"] = cachedInputTokens,
            ["cachedInputReadTokens"] = cachedInputReadTokens,
            ["cachedInputWriteTokens"] = cachedInputWriteTokens,
            ["runtimeMs"] = runtimeMs,
            ["gateway"] = gateway
        };

        if (additionalProperties is not null)
        {
            foreach (var item in additionalProperties)
                metadata[item.Key] = item.Value;
        }

        return JsonSerializer.SerializeToElement(metadata, Json)
            .Deserialize<AIFinishMessageMetadata>(Json)
            ?? throw new JsonException("Failed to create unified finish metadata.");
    }

    public static AIFinishMessageMetadata FromDictionary(
        Dictionary<string, object>? metadata,
        string? fallbackModel = null,
        DateTimeOffset? fallbackTimestamp = null)
    {
        var merged = metadata?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value) ?? [];

        EnsureUsage(merged);

        if (!merged.ContainsKey("model") && !string.IsNullOrWhiteSpace(fallbackModel))
            merged["model"] = fallbackModel;

        if (!merged.ContainsKey("timestamp"))
            merged["timestamp"] = fallbackTimestamp ?? DateTimeOffset.UtcNow;

        return JsonSerializer.SerializeToElement(merged, Json)
            .Deserialize<AIFinishMessageMetadata>(Json)
            ?? throw new JsonException("Failed to deserialize unified finish metadata.");
    }

    public static implicit operator AIFinishMessageMetadata?(Dictionary<string, object>? metadata)
        => metadata is null ? null : FromDictionary(metadata);

    private static void EnsureUsage(Dictionary<string, object?> metadata)
    {
        metadata["usage"] = NormalizeUsage(
            metadata.TryGetValue("usage", out var usage) ? usage : null,
            ExtractInt(metadata, "inputTokens"),
            ExtractInt(metadata, "outputTokens"),
            ExtractInt(metadata, "totalTokens"));
    }

    private static object NormalizeUsage(object? usage, int? inputTokens, int? outputTokens, int? totalTokens)
    {
        if (TryGetObjectLikeUsage(usage, out var normalizedUsage))
            return normalizedUsage;

        if (inputTokens is null && outputTokens is null && totalTokens is null)
            return EmptyUsage.Clone();

        return new Dictionary<string, object?>
        {
            ["inputTokens"] = inputTokens,
            ["outputTokens"] = outputTokens,
            ["totalTokens"] = totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0))
        };
    }

    private static bool TryGetObjectLikeUsage(object? usage, out object normalizedUsage)
    {
        switch (usage)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.Object:
                normalizedUsage = json.Clone();
                return true;
            case Dictionary<string, object?> dictionary:
                normalizedUsage = dictionary;
                return true;
            case not null:
                try
                {
                    var serializedUsage = JsonSerializer.SerializeToElement(usage, Json);
                    if (serializedUsage.ValueKind == JsonValueKind.Object)
                    {
                        normalizedUsage = serializedUsage;
                        return true;
                    }
                }
                catch
                {
                }

                break;
            default:
                break;
        }

        normalizedUsage = EmptyUsage.Clone();
        return false;
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

}

public sealed class AIFinishEventData
{
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("messageMetadata")]
    public AIFinishMessageMetadata? MessageMetadata { get; init; }

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
