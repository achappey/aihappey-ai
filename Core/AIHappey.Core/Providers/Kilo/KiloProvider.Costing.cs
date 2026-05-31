using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Kilo;

public partial class KiloProvider
{
    private static ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response)
    {
        var cost = TryGetUsageCost(response.Usage);

        response.Usage = UpsertUsageCost(response.Usage, cost);
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            cost);

        return response;
    }

    private static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update)
    {
        var cost = TryGetUsageCost(update.Usage);

        update.Usage = UpsertUsageCost(update.Usage, cost);
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            cost);

        return update;
    }

    private static object? UpsertUsageCost(object? usage, decimal? cost)
    {
        if (!cost.HasValue || usage is null)
            return usage;

        var usageElement = usage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
        };

        if (usageElement.ValueKind != JsonValueKind.Object)
            return usage;

        var usageData = usageElement.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web)
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        usageData["cost"] = JsonSerializer.SerializeToElement(cost.Value, JsonSerializerOptions.Web);

        return JsonSerializer.SerializeToElement(usageData, JsonSerializerOptions.Web);
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostToChatCompletionMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        decimal? cost)
    {
        if (!cost.HasValue)
            return additionalProperties;

        var enrichedAdditionalProperties = additionalProperties is not null
            ? new Dictionary<string, JsonElement>(additionalProperties, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, JsonElement>? existingMetadata = null;
        if (additionalProperties is not null
            && additionalProperties.TryGetValue("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object)
        {
            existingMetadata = metadataElement.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web);
        }

        enrichedAdditionalProperties["metadata"] = JsonSerializer.SerializeToElement(
            ModelCostMetadataEnricher.AddCost(existingMetadata, cost),
            JsonSerializerOptions.Web);

        return enrichedAdditionalProperties;
    }

    private static decimal? TryGetUsageCost(object? usage)
    {
        if (usage is null)
            return null;

        var usageElement = usage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
        };

        if (usageElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(usageElement, "cost_details", out var costDetailsElement)
            || costDetailsElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(costDetailsElement, "upstream_inference_cost", out var upstreamInferenceCostElement))
        {
            return null;
        }

        return TryGetDecimal(upstreamInferenceCostElement);
    }

    private static decimal? TryGetDecimal(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
