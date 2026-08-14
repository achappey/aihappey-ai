using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static AIUsage? ToUnifiedUsage(object? value)
    {
        var usage = ToResponseUsageObject(value);
        return usage is null
            ? null
            : new AIUsage
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens
                    ?? (usage.InputTokens is not null && usage.OutputTokens is not null
                        ? usage.InputTokens + usage.OutputTokens
                        : null),
                CachedInputTokens = usage.InputTokensDetails?.CachedTokens,
                CacheWriteInputTokens = usage.InputTokensDetails?.CacheWriteTokens,
                ReasoningTokens = usage.OutputTokensDetails?.ReasoningTokens
            };
    }

    private static ResponseUsage? ToResponseUsage(object? value)
    {
        if (value is null)
            return null;

        if (value is ResponseUsage responseUsage)
            return responseUsage;

        if (value is AIUsage usage)
            return ToResponseUsage(usage);

        return ToResponseUsageObject(value);
    }

    private static ResponseUsage ToResponseUsage(AIUsage usage)
        => usage is null
            ? new ResponseUsage()
            : new ResponseUsage
            {
                InputTokens = usage.InputTokens,
                InputTokensDetails = usage.CachedInputTokens is not null || usage.CacheWriteInputTokens is not null
                    ? new ResponseInputTokensDetails
                    {
                        CachedTokens = usage.CachedInputTokens,
                        CacheWriteTokens = usage.CacheWriteInputTokens
                    }
                    : null,
                OutputTokens = usage.OutputTokens,
                OutputTokensDetails = usage.ReasoningTokens is not null
                    ? new ResponseOutputTokensDetails { ReasoningTokens = usage.ReasoningTokens }
                    : null,
                TotalTokens = usage.TotalTokens
                    ?? (usage.InputTokens is not null && usage.OutputTokens is not null
                        ? usage.InputTokens + usage.OutputTokens
                        : null)
            };

    private static ResponseUsage? ToResponseUsageObject(object? value)
    {
        if (value is null)
            return null;

        if (value is ResponseUsage usage)
            return usage;

        try
        {
            var raw = value is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(value, Json);
            return raw.ValueKind == JsonValueKind.Object
                ? raw.Deserialize<ResponseUsage>(ResponseJson.Default)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static object GetRawUsage(object usage)
    {
        var normalized = ToResponseUsageObject(usage);
        if (normalized?.Raw is { } raw && raw.ValueKind == JsonValueKind.Object)
            return raw.Clone();

        return usage is JsonElement element
            ? element.Clone()
            : JsonSerializer.SerializeToElement(usage, Json);
    }

    private static Dictionary<string, object?> AddRawUsageMetadata(
        Dictionary<string, object?>? metadata,
        string providerId,
        object? usage)
    {
        var result = metadata?.ToDictionary(entry => entry.Key, entry => entry.Value) ?? [];
        if (usage is null || string.IsNullOrWhiteSpace(providerId))
            return result;

        var providerMetadata = ToObjectDictionary(
            result.TryGetValue(providerId, out var existingProviderMetadata)
                ? existingProviderMetadata
                : null);
        providerMetadata["usage"] = GetRawUsage(usage);
        result[providerId] = providerMetadata;
        return result;
    }

    private static Dictionary<string, object?> ToObjectDictionary(object? value)
    {
        if (value is Dictionary<string, object?> nullableDictionary)
            return nullableDictionary.ToDictionary(entry => entry.Key, entry => entry.Value);

        if (value is Dictionary<string, object> dictionary)
            return dictionary.ToDictionary(entry => entry.Key, entry => (object?)entry.Value);

        try
        {
            var json = value is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(value, Json);
            return json.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(json.GetRawText(), Json) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }
}
