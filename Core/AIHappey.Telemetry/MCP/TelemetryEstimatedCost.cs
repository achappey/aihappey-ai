using System.Collections.Concurrent;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Telemetry.MCP;

public static class TelemetryEstimatedCost
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, ModelPricing>> PricingByProvider =
        new(StringComparer.OrdinalIgnoreCase);

    public static decimal? Calculate(
        string? provider,
        string? model,
        int? inputTokens,
        int? outputTokens)
    {
        if (string.IsNullOrWhiteSpace(provider)
            || string.IsNullOrWhiteSpace(model)
            || inputTokens is null or < 0
            || outputTokens is null or < 0)
        {
            return null;
        }

        if (inputTokens == 0 && outputTokens == 0)
            return null;

        var pricing = PricingByProvider.GetOrAdd(
            provider.Trim(),
            providerId => providerId.GetPricing() ?? []);
        if (pricing is null || pricing.Count == 0)
            return null;

        foreach (var candidate in GetModelCandidates(provider, model))
        {
            var match = pricing.TryGetValue(candidate, out var exactMatch)
                ? exactMatch
                : pricing.FirstOrDefault(item =>
                    string.Equals(item.Key, candidate, StringComparison.OrdinalIgnoreCase)).Value;
            if (match is not null)
            {
                return ModelCostMetadataEnricher.ComputeCost(
                    match,
                    inputTokens.Value,
                    outputTokens.Value,
                    cachedInputReadTokens: 0,
                    cachedInputWriteTokens: 0);
            }
        }

        return null;
    }

    private static IEnumerable<string> GetModelCandidates(string provider, string model)
    {
        var trimmedModel = model.Trim();
        yield return trimmedModel;

        var providerPrefix = provider.Trim() + "/";
        if (!trimmedModel.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            yield return providerPrefix + trimmedModel;

        var separator = trimmedModel.IndexOf('/');
        if (separator >= 0 && separator < trimmedModel.Length - 1)
        {
            var modelName = trimmedModel[(separator + 1)..];
            yield return modelName;
            if (!modelName.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
                yield return providerPrefix + modelName;
        }
    }
}
