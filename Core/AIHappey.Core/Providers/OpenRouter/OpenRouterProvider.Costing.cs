using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private static ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response)
    {
        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            TryGetUsageCost(response.Usage));

        return response;
    }

    private static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update)
    {
        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            TryGetUsageCost(update.Usage));

        return update;
    }

    private static ResponseResult EnrichResponseWithGatewayCost(ResponseResult response)
    {
        response.Metadata = ModelCostMetadataEnricher.AddCost(
            response.Metadata,
            TryGetUsageCost(response.Usage));

        return response;
    }

    public static ChatCompletion EnrichChatCompletionWithGatewayCostForTests(ChatCompletion response)
        => EnrichChatCompletionWithGatewayCost(response);

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCostForTests(ChatCompletionUpdate update)
        => EnrichChatCompletionUpdateWithGatewayCost(update);

    public static ResponseResult EnrichResponseWithGatewayCostForTests(ResponseResult response)
        => EnrichResponseWithGatewayCost(response);

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
            || !TryGetOpenRouterProperty(usageElement, "cost", out var costElement))
        {
            return null;
        }

        return TryGetOpenRouterDecimal(costElement);
    }

    private static decimal? TryGetOpenRouterDecimal(JsonElement element)
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

    private static bool TryGetOpenRouterProperty(JsonElement element, string propertyName, out JsonElement value)
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
