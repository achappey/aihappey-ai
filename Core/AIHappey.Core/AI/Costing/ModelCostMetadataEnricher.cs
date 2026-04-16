using System.Globalization;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class ModelCostMetadataEnricher
{
    public static FinishMessageMetadata? AddCost(FinishMessageMetadata? metadata, ModelPricing? pricing)
    {
        if (pricing == null || metadata == null)
            return metadata;

        var usage = GetProviderUsage(metadata);
        var enriched = AddCostFromUsage(usage, metadata.ToDictionary(), pricing);

        return FinishMessageMetadata.FromDictionary(
            enriched.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            fallbackModel: metadata.Model,
            fallbackTimestamp: metadata.Timestamp);
    }

    public static FinishUIPart AddCost(FinishUIPart finish, ModelPricing? pricing)
    {
        if (pricing == null || finish.MessageMetadata == null)
            return finish;

        return new FinishUIPart
        {
            FinishReason = finish.FinishReason,
            MessageMetadata = AddCost(finish.MessageMetadata, pricing)
        };
    }

    public static Dictionary<string, object?> AddCost(
        Dictionary<string, object?>? existingMetadata,
        decimal? cost)
    {
        var metadata = existingMetadata != null
            ? new Dictionary<string, object?>(existingMetadata)
            : [];

        if (!cost.HasValue)
            return metadata;

        if (!metadata.TryGetValue("gateway", out var gatewayObj) || gatewayObj is not Dictionary<string, object?> gateway)
        {
            gateway = new Dictionary<string, object?>();
            metadata["gateway"] = gateway;
        }

        gateway["cost"] = cost.Value;
        return metadata;
    }

    public static Dictionary<string, JsonElement>? AddCost(
        Dictionary<string, JsonElement>? existingMetadata,
        decimal? cost)
    {
        var enriched = AddCost(ToObjectMetadata(existingMetadata), cost);

        return JsonSerializer.SerializeToElement(enriched, JsonSerializerOptions.Web)
            .Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web);
    }

    public static Dictionary<string, object?> AddCostFromUsage(
        object? usage,
        Dictionary<string, object?>? existingMetadata,
        ModelPricing? pricing)
    {
        if (pricing == null || usage == null)
            return existingMetadata != null
                ? new Dictionary<string, object?>(existingMetadata)
                : [];

        if (!TryGetUsageInt(usage, "input_tokens", out var inputTokens)
            && !TryGetUsageInt(usage, "inputTokens", out inputTokens)
            && !TryGetUsageInt(usage, "prompt_tokens", out inputTokens)
            && !TryGetUsageInt(usage, "promptTokens", out inputTokens))
        {
            return existingMetadata != null
                ? new Dictionary<string, object?>(existingMetadata)
                : [];
        }

        TryGetUsageInt(usage, "output_tokens", out var outputTokens);
        if (outputTokens == 0)
            TryGetUsageInt(usage, "outputTokens", out outputTokens);
        if (outputTokens == 0)
            TryGetUsageInt(usage, "completion_tokens", out outputTokens);
        if (outputTokens == 0)
            TryGetUsageInt(usage, "completionTokens", out outputTokens);

        TryGetUsageInt(usage, "total_tokens", out var totalTokens);
        if (totalTokens == 0)
            TryGetUsageInt(usage, "totalTokens", out totalTokens);

        if (!TryGetUsageInt(usage, "cached_input_tokens", out int cachedInputReadTokens)
            && !TryGetUsageInt(usage, "cachedInputTokens", out cachedInputReadTokens))
        {
            TryGetNestedUsageInt(usage, "input_tokens_details", "cached_tokens", out cachedInputReadTokens);
        }

        if (outputTokens == 0 && totalTokens > 0)
            outputTokens = Math.Max(0, totalTokens - inputTokens - cachedInputReadTokens);

        var cost = ComputeCost(pricing, inputTokens, outputTokens, cachedInputReadTokens, 0);
        return AddCost(existingMetadata, cost);
    }

    public static int? GetTotalTokens(object? usage)
    {
        if (usage == null)
            return null;

        if (TryGetUsageInt(usage, "total_tokens", out var totalTokens) && totalTokens > 0)
            return totalTokens;

        if (TryGetUsageInt(usage, "totalTokens", out totalTokens) && totalTokens > 0)
            return totalTokens;

        return null;
    }

    public static decimal ComputeCost(
        ModelPricing pricing,
        int inputTokens,
        int outputTokens,
        int cachedInputReadTokens,
        int cachedInputWriteTokens)
    {
        decimal cost = (inputTokens * pricing.Input) + (outputTokens * pricing.Output);

        if (cachedInputReadTokens > 0 && pricing.InputCacheRead.HasValue)
            cost += cachedInputReadTokens * pricing.InputCacheRead.Value;

        if (cachedInputWriteTokens > 0 && pricing.InputCacheWrite.HasValue)
            cost += cachedInputWriteTokens * pricing.InputCacheWrite.Value;

        return cost;
    }

    private static Dictionary<string, object?>? ToObjectMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        return JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            .Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web);
    }

    private static object? GetProviderUsage(FinishMessageMetadata metadata)
    {
        if (metadata.AdditionalProperties is not null)
        {
            foreach (var item in metadata.AdditionalProperties)
            {
                if (item.Value.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.Value.TryGetProperty("usage", out var providerUsage)
                    && providerUsage.ValueKind == JsonValueKind.Object)
                {
                    return providerUsage.Clone();
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["promptTokens"] = metadata.Usage.PromptTokens,
            ["completionTokens"] = metadata.Usage.CompletionTokens,
            ["totalTokens"] = metadata.Usage.TotalTokens
        };
    }

    private static bool TryGetUsageInt(object usage, string key, out int value)
    {
        value = 0;

        return usage switch
        {
            JsonElement json => TryGetInt(json, key, out value),
            Dictionary<string, object> dict => TryGetInt(dict, key, out value),
            _ => false
        };
    }

    private static bool TryGetNestedUsageInt(object usage, string parentKey, string nestedKey, out int value)
    {
        value = 0;

        if (usage is JsonElement json
            && TryGetProperty(json, parentKey, out var parent)
            && parent.ValueKind == JsonValueKind.Object
            && TryGetProperty(parent, nestedKey, out var nested))
        {
            return TryGetInt(nested, out value);
        }

        return false;
    }

    private static bool TryGetInt(Dictionary<string, object> metadata, string key, out int value)
    {
        value = 0;
        return metadata.TryGetValue(key, out var raw) && TryConvertToInt(raw, out value);
    }

    private static bool TryGetInt(JsonElement json, string key, out int value)
    {
        value = 0;
        if (!TryGetProperty(json, key, out var prop))
            return false;

        return TryGetInt(prop, out value);
    }

    private static bool TryGetInt(JsonElement json, out int value)
    {
        value = 0;

        return json.ValueKind switch
        {
            JsonValueKind.Number when json.TryGetInt32(out var jsonInt) => (value = jsonInt) >= 0 || jsonInt < 0,
            JsonValueKind.String when int.TryParse(json.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (value = parsed) >= 0 || parsed < 0,
            _ => false
        };
    }

    private static bool TryGetProperty(JsonElement json, string key, out JsonElement value)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in json.EnumerateObject())
            {
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryConvertToInt(object? raw, out int value)
    {
        value = 0;

        switch (raw)
        {
            case int intVal:
                value = intVal;
                return true;
            case long longVal when longVal >= int.MinValue && longVal <= int.MaxValue:
                value = (int)longVal;
                return true;
            case decimal decVal:
                value = (int)decVal;
                return true;
            case double doubleVal:
                value = (int)doubleVal;
                return true;
            case float floatVal:
                value = (int)floatVal;
                return true;
            case string strVal when int.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            case JsonElement json:
                return TryGetInt(json, out value);
            default:
                return false;
        }
    }
}
