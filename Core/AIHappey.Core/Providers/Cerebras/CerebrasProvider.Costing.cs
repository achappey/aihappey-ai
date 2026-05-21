using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cerebras;

public partial class CerebrasProvider
{
    private ChatCompletion EnrichChatCompletionWithGatewayCost(ChatCompletion response, string? requestModel)
    {
        var pricing = ResolveCatalogPricing(string.IsNullOrWhiteSpace(response.Model)
            ? requestModel
            : response.Model);

        response.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            response.AdditionalProperties,
            response.Usage,
            pricing);

        return response;
    }

    private ChatCompletionUpdate EnrichChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update, string? requestModel)
    {
        var pricing = ResolveCatalogPricing(string.IsNullOrWhiteSpace(update.Model)
            ? requestModel
            : update.Model);

        update.AdditionalProperties = AddGatewayCostToChatCompletionMetadata(
            update.AdditionalProperties,
            update.Usage,
            pricing);

        return update;
    }

    private ModelPricing? ResolveCatalogPricing(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var pricing = GetIdentifier().GetPricing();
        if (pricing == null || pricing.Count == 0)
            return null;

        return pricing.TryGetValue(modelId, out var modelPricing)
            ? modelPricing
            : null;
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostToChatCompletionMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        object? usage,
        ModelPricing? pricing)
    {
        if (usage is null || pricing is null)
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
            ModelCostMetadataEnricher.AddCostFromUsage(usage, existingMetadata, pricing),
            JsonSerializerOptions.Web);

        return enrichedAdditionalProperties;
    }
}
