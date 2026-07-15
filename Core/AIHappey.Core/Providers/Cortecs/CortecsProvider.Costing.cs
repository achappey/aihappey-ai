using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    private const decimal CortecsUsageCostScale = 1_000_000m;

    private static ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response)
    {
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            TryGetCortecsGatewayCost(response.Usage));

        return response;
    }

    private static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update)
    {
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            TryGetCortecsGatewayCost(update.Usage));

        return update;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(ChatCompletion response)
        => EnrichChatCompletionWithGatewayCost(response);

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(ChatCompletionUpdate update)
        => EnrichChatCompletionUpdateWithGatewayCost(update);

    public static ChatCompletionUpdate NormalizeStreamingUpdateForGatewayCostForTests(
        ChatCompletionUpdate update,
        ref string? lastFinishReason)
    {
        CatalogPricingCostingExtensions.NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);
        return update;
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

    private static decimal? TryGetCortecsGatewayCost(object? usage)
    {
        if (!TryGetCortecsRawUsageCost(usage, out var rawCost))
            return null;

        return rawCost / CortecsUsageCostScale;
    }

    private static bool TryGetCortecsRawUsageCost(object? usage, out decimal cost)
    {
        cost = 0m;

        if (usage is null)
            return false;

        var usageElement = usage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
        };

        if (usageElement.ValueKind != JsonValueKind.Object
            || !TryGetCortecsProperty(usageElement, "cost", out var costElement))
        {
            return false;
        }

        return TryGetCortecsDecimal(costElement, out cost);
    }

    private static bool TryGetCortecsDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => (value = parsed) >= 0 || parsed < 0,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed) => (value = parsed) >= 0 || parsed < 0,
            _ => false
        };
    }

    private static bool TryGetCortecsProperty(JsonElement element, string propertyName, out JsonElement value)
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

