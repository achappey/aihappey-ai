using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Unified.Models;

/// <summary>
/// Protocol-neutral token usage carried by the unified response model.
/// Provider-specific usage belongs in provider-scoped response metadata.
/// </summary>
public sealed class AIUsage
{
    [JsonPropertyName("inputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalTokens { get; init; }

    [JsonPropertyName("cachedInputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CachedInputTokens { get; init; }

    [JsonPropertyName("cacheWriteInputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CacheWriteInputTokens { get; init; }

    [JsonPropertyName("reasoningTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReasoningTokens { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
