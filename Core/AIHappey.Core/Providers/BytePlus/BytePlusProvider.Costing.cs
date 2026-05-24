using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.BytePlus;

public partial class BytePlusProvider
{
    private ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response, string? requestModel)
    {
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            response.Usage,
            string.IsNullOrWhiteSpace(response.Model) ? requestModel : response.Model);

        return response;
    }

    private ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update, string? requestModel)
    {
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            update.Usage,
            string.IsNullOrWhiteSpace(update.Model) ? requestModel : update.Model);

        return update;
    }

    private Responses.ResponseResult EnrichResponseWithGatewayCost(Responses.ResponseResult response, string? requestModel)
    {
        response.Metadata = AddGatewayCostFromUsage(
            response.Usage,
            response.Metadata,
            string.IsNullOrWhiteSpace(response.Model) ? requestModel : response.Model);

        return response;
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostToChatCompletionMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        object? usage,
        string? modelId)
    {
        if (usage is null)
            return additionalProperties;

        var enrichedAdditionalProperties = additionalProperties is not null
            ? new Dictionary<string, JsonElement>(additionalProperties, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, object?>? existingMetadata = null;
        if (additionalProperties is not null
            && additionalProperties.TryGetValue("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object)
        {
            existingMetadata = metadataElement.Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web);
        }

        enrichedAdditionalProperties["metadata"] = JsonSerializer.SerializeToElement(
            AddGatewayCostFromUsage(usage, existingMetadata, modelId),
            JsonSerializerOptions.Web);

        return enrichedAdditionalProperties;
    }

    private static Dictionary<string, object?> AddGatewayCostFromUsage(
        object? usage,
        Dictionary<string, object?>? existingMetadata,
        string? modelId)
    {
        if (usage is null)
            return existingMetadata is not null
                ? new Dictionary<string, object?>(existingMetadata)
                : [];

        if (!TryGetUsageInt(usage, "input_tokens", out var inputTokens)
            && !TryGetUsageInt(usage, "inputTokens", out inputTokens)
            && !TryGetUsageInt(usage, "prompt_tokens", out inputTokens)
            && !TryGetUsageInt(usage, "promptTokens", out inputTokens))
        {
            return existingMetadata is not null
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

        if (!TryGetUsageInt(usage, "cached_input_tokens", out var cachedInputReadTokens)
            && !TryGetUsageInt(usage, "cachedInputTokens", out cachedInputReadTokens))
        {
            TryGetNestedUsageInt(usage, "input_tokens_details", "cached_tokens", out cachedInputReadTokens);
        }

        if (outputTokens == 0 && totalTokens > 0)
            outputTokens = Math.Max(0, totalTokens - inputTokens - cachedInputReadTokens);

        var pricing = BytePlusTieredPricingResolver.Resolve(modelId, inputTokens);
        if (pricing is null)
            return existingMetadata is not null
                ? new Dictionary<string, object?>(existingMetadata)
                : [];

        var cost = ModelCostMetadataEnricher.ComputeCost(
            pricing,
            inputTokens,
            outputTokens,
            cachedInputReadTokens,
            cachedInputWriteTokens: 0);

        return ModelCostMetadataEnricher.AddCost(existingMetadata, cost);
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
            case JsonElement json:
                return TryGetInt(json, out value);
            case string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }
}
