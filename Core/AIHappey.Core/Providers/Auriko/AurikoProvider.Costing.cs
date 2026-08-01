using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Auriko;

public partial class AurikoProvider
{
    private static ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response)
    {
        var cost = GetGatewayCost(response.Usage);

        response.Usage = UpsertUsageCost(response.Usage, cost);
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            cost);

        return response;
    }

    private static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update)
    {
        var cost = GetGatewayCost(update.Usage);

        update.Usage = UpsertUsageCost(update.Usage, cost);
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            cost);

        return update;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(ChatCompletion response)
        => EnrichChatCompletionWithGatewayCost(response);

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(ChatCompletionUpdate update)
        => EnrichChatCompletionUpdateWithGatewayCost(update);

    public static decimal? GetGatewayCost(object? usage)
    {
        if (usage is null)
            return null;

        try
        {
            var usageElement = usage switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
            };

            if (usageElement.ValueKind != JsonValueKind.Object
                || !TryGetAurikoProperty(usageElement, "estimated_cost", out var costElement))
            {
                return null;
            }

            return TryGetAurikoDecimal(costElement);
        }
        catch
        {
            return null;
        }
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

    private static decimal? TryGetAurikoDecimal(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };

    private static bool TryGetAurikoProperty(JsonElement element, string propertyName, out JsonElement value)
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
