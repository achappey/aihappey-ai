using System.Text.Json;

namespace AIHappey.Core.MCP.Telemetry;

public static class McpResponsesUsageExtractor
{
    public static McpResponsesTokenCounts GetTokenCounts(object? usageObj)
    {
        if (usageObj is null)
            return new McpResponsesTokenCounts(0, 0);

        if (usageObj is JsonElement usage)
            return GetTokenCounts(usage);

        var serializedUsage = JsonSerializer.SerializeToElement(usageObj, JsonSerializerOptions.Web);
        return GetTokenCounts(serializedUsage);
    }

    private static McpResponsesTokenCounts GetTokenCounts(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return new McpResponsesTokenCounts(0, 0);

        var inputTokens = GetUsageTokenCount(usage, "input_tokens") ?? 0;
        var totalTokens = GetUsageTokenCount(usage, "total_tokens") ?? 0;

        return new McpResponsesTokenCounts(inputTokens, totalTokens);
    }

    private static int? GetUsageTokenCount(JsonElement usage, string name)
    {
        foreach (var property in usage.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }
}

public sealed record McpResponsesTokenCounts(int InputTokens, int TotalTokens);

