using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static class MessagesUsageExtensions
{
    /// <summary>
    /// Messages input_tokens excludes cache reads and writes. Unified input includes
    /// all three categories, so total minus input always represents actual output.
    /// The aggregate cache-creation count and its TTL breakdown are alternatives,
    /// not separate usage to add together.
    /// </summary>
    public static AIUsage? ToUnifiedUsage(this MessagesUsage? usage)
    {
        if (usage is null)
            return null;

        var cacheWrite = usage.CacheCreationInputTokens
            ?? (usage.CacheCreation is null ? (int?)null
                : (usage.CacheCreation.Ephemeral1hInputTokens ?? 0)
                    + (usage.CacheCreation.Ephemeral5mInputTokens ?? 0));
        var input = usage.InputTokens is null && usage.CacheReadInputTokens is null && cacheWrite is null
            ? (int?)null
            : (usage.InputTokens ?? 0) + (usage.CacheReadInputTokens ?? 0) + (cacheWrite ?? 0);

        return new AIUsage
        {
            InputTokens = input,
            OutputTokens = usage.OutputTokens,
            TotalTokens = input is null && usage.OutputTokens is null
                ? null : (input ?? 0) + (usage.OutputTokens ?? 0),
            CachedInputTokens = usage.CacheReadInputTokens,
            CacheWriteInputTokens = cacheWrite
        };
    }

    internal static MessagesUsage ToMessagesUsage(this AIUsage usage) => new()
    {
        InputTokens = usage.InputTokens is null ? null
            : Math.Max(0, usage.InputTokens.Value
                - (usage.CachedInputTokens ?? 0) - (usage.CacheWriteInputTokens ?? 0)),
        OutputTokens = usage.OutputTokens,
        CacheReadInputTokens = usage.CachedInputTokens,
        CacheCreationInputTokens = usage.CacheWriteInputTokens
    };
}
