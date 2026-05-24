using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider
{
    private ChatCompletion EnrichAgentChatCompletionWithGatewayCost(ChatCompletion response, string? requestModel)
    {
        var pricing = ResolveAgentCatalogPricing(string.IsNullOrWhiteSpace(response.Model)
            ? requestModel
            : response.Model);

        response.AdditionalProperties = AddGatewayCostToAgentChatCompletionMetadata(
            response.AdditionalProperties,
            response.Usage,
            pricing);

        return response;
    }

    private ChatCompletionUpdate EnrichAgentChatCompletionUpdateWithGatewayCost(ChatCompletionUpdate update, string? requestModel)
    {
        var pricing = ResolveAgentCatalogPricing(string.IsNullOrWhiteSpace(update.Model)
            ? requestModel
            : update.Model);

        update.AdditionalProperties = AddGatewayCostToAgentChatCompletionMetadata(
            update.AdditionalProperties,
            update.Usage,
            pricing);

        return update;
    }

    private ModelPricing? ResolveAgentCatalogPricing(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var pricing = GetIdentifier().GetPricing();
        if (pricing is null || pricing.Count == 0)
            return null;

        foreach (var candidate in GetAgentPricingLookupCandidates(modelId))
        {
            if (pricing.TryGetValue(candidate, out var modelPricing))
                return modelPricing;
        }

        return null;
    }

    private static IEnumerable<string> GetAgentPricingLookupCandidates(string modelId)
    {
        var trimmed = modelId.Trim();
        if (trimmed.Length == 0)
            yield break;

        yield return trimmed;

        if (trimmed.StartsWith("zai/", StringComparison.OrdinalIgnoreCase))
        {
            var withoutProvider = trimmed["zai/".Length..];
            yield return withoutProvider;

            if (withoutProvider.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
                yield return $"zai/{withoutProvider}";

            yield break;
        }

        if (trimmed.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            yield return $"zai/{trimmed}";
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostToAgentChatCompletionMetadata(
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
