using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Google;

internal static class GoogleTieredPricingResolver
{
    private const string PricingDirectoryName = "google";
    private const string ProviderIdentifier = "google";

    private static readonly Lazy<IReadOnlyDictionary<PricingVariantKey, IReadOnlyDictionary<string, ModelPricing>>> TieredPricing =
        new(LoadTieredPricing);

    public static ModelPricing? Resolve(string? resolvedModelId, string? serviceTier, int? _)
    {
        var defaultPricing = GetDefaultPricing(resolvedModelId);

        if (defaultPricing is null || string.IsNullOrWhiteSpace(resolvedModelId))
            return defaultPricing;

        var normalizedTier = NormalizeTier(serviceTier);
        if (TryResolveTieredPricing(resolvedModelId, normalizedTier, out var tieredPricing))
            return tieredPricing;

        return defaultPricing;
    }

    private static string? NormalizeTier(string? serviceTier)
    {
        if (string.IsNullOrWhiteSpace(serviceTier))
            return null;

        var normalized = serviceTier.Trim().ToLowerInvariant();

        return normalized switch
        {
            "auto" or "default" or "standard" => null,
            _ => normalized
        };
    }

    private static IReadOnlyDictionary<PricingVariantKey, IReadOnlyDictionary<string, ModelPricing>> LoadTieredPricing()
    {
        var directoryPath = Path.Combine(AppContext.BaseDirectory, "Catalog", "Pricing", "providers", PricingDirectoryName);
        if (!Directory.Exists(directoryPath))
        {
            return new Dictionary<PricingVariantKey, IReadOnlyDictionary<string, ModelPricing>>();
        }

        var result = new Dictionary<PricingVariantKey, IReadOnlyDictionary<string, ModelPricing>>();

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!TryParsePricingVariant(filePath, out var pricingVariant))
                continue;

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
                continue;

            using var doc = JsonDocument.Parse(json);
            var perModelPricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

            foreach (var modelNode in doc.RootElement.EnumerateObject())
            {
                if (!modelNode.Value.TryGetProperty("pricing", out var pricingNode))
                    continue;

                var pricing = pricingNode.Deserialize<ModelPricing>(JsonSerializerOptions.Web);
                if (pricing is not null)
                    perModelPricing[modelNode.Name] = pricing;
            }

            result[pricingVariant] = perModelPricing;
        }

        return result;
    }

    private static bool TryResolveTieredPricing(string resolvedModelId, string? serviceTier, out ModelPricing? pricing)
    {
        pricing = null;

        var key = new PricingVariantKey(serviceTier);
        if (!TieredPricing.Value.TryGetValue(key, out var tierPricing))
            return false;

        foreach (var candidateModelId in GetModelLookupCandidates(resolvedModelId))
        {
            if (tierPricing.TryGetValue(candidateModelId, out var exactMatch))
            {
                pricing = exactMatch;
                return true;
            }
        }

        var candidates = GetModelLookupCandidates(resolvedModelId);
        var fallbackMatch = tierPricing.FirstOrDefault(x =>
            candidates.Any(candidateModelId =>
                string.Equals(x.Key, candidateModelId, StringComparison.OrdinalIgnoreCase)
                || x.Key.EndsWith(candidateModelId, StringComparison.OrdinalIgnoreCase)
                || candidateModelId.EndsWith(x.Key, StringComparison.OrdinalIgnoreCase)));

        if (string.IsNullOrWhiteSpace(fallbackMatch.Key))
            return false;

        pricing = fallbackMatch.Value;
        return true;
    }

    private static bool TryParsePricingVariant(string filePath, out PricingVariantKey pricingVariant)
    {
        pricingVariant = default;

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var parts = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        pricingVariant = new PricingVariantKey(NormalizeTier(parts[0]));
        return true;
    }

    private static ModelPricing? GetDefaultPricing(string? resolvedModelId)
    {
        if (string.IsNullOrWhiteSpace(resolvedModelId))
            return null;

        var pricing = ProviderIdentifier.GetPricing();
        if (pricing is null || pricing.Count == 0)
            return null;

        foreach (var candidateModelId in GetModelLookupCandidates(resolvedModelId))
        {
            if (pricing.TryGetValue(candidateModelId, out var exactMatch))
                return exactMatch;
        }

        var candidates = GetModelLookupCandidates(resolvedModelId);
        var fallbackMatch = pricing.FirstOrDefault(x =>
            candidates.Any(candidateModelId =>
                string.Equals(x.Key, candidateModelId, StringComparison.OrdinalIgnoreCase)
                || x.Key.EndsWith(candidateModelId, StringComparison.OrdinalIgnoreCase)
                || candidateModelId.EndsWith(x.Key, StringComparison.OrdinalIgnoreCase)));

        return string.IsNullOrWhiteSpace(fallbackMatch.Key)
            ? null
            : fallbackMatch.Value;
    }

    private static string[] GetModelLookupCandidates(string resolvedModelId)
    {
        var trimmed = resolvedModelId.Trim();
        if (trimmed.StartsWith(ProviderIdentifier + "/", StringComparison.OrdinalIgnoreCase))
            return [trimmed, trimmed[(ProviderIdentifier.Length + 1)..]];

        return [trimmed, ProviderIdentifier + "/" + trimmed];
    }

    private readonly record struct PricingVariantKey(string? Tier);
}
