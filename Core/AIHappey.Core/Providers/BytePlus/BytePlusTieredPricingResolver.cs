using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.BytePlus;

internal static class BytePlusTieredPricingResolver
{
    private const string ProviderIdentifier = "byteplus";
    private const int DefaultLongContextTokenThreshold = 128000;
    private const int DeepSeekLongContextTokenThreshold = 32000;

    private static readonly Lazy<IReadOnlyDictionary<string, ModelPricing>> LongContextPricing = new(LoadLongContextPricing);

    public static ModelPricing? Resolve(string? resolvedModelId, int promptTokens)
    {
        if (string.IsNullOrWhiteSpace(resolvedModelId))
            return null;

        var defaultPricing = GetDefaultPricing(resolvedModelId);
        if (defaultPricing is null)
            return null;

        if (!ShouldUseLongContextPricing(resolvedModelId, promptTokens))
            return defaultPricing;

        return TryResolvePricing(LongContextPricing.Value, resolvedModelId, out var longContextPricing)
            ? longContextPricing
            : defaultPricing;
    }

    private static bool ShouldUseLongContextPricing(string modelId, int promptTokens)
        => promptTokens > GetLongContextTokenThreshold(modelId);

    private static int GetLongContextTokenThreshold(string modelId)
    {
        foreach (var candidate in GetModelLookupCandidates(modelId))
        {
            if (string.Equals(candidate, "byteplus/deepseek-v3-2-251201", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, "deepseek-v3-2-251201", StringComparison.OrdinalIgnoreCase))
            {
                return DeepSeekLongContextTokenThreshold;
            }
        }

        return DefaultLongContextTokenThreshold;
    }

    private static ModelPricing? GetDefaultPricing(string resolvedModelId)
    {
        var pricing = ProviderIdentifier.GetPricing();
        if (pricing is null || pricing.Count == 0)
            return null;

        return TryResolvePricing(pricing, resolvedModelId, out var modelPricing)
            ? modelPricing
            : null;
    }

    private static bool TryResolvePricing(
        IReadOnlyDictionary<string, ModelPricing> pricing,
        string resolvedModelId,
        out ModelPricing? modelPricing)
    {
        modelPricing = null;

        foreach (var candidateModelId in GetModelLookupCandidates(resolvedModelId))
        {
            if (pricing.TryGetValue(candidateModelId, out var exactMatch))
            {
                modelPricing = exactMatch;
                return true;
            }
        }

        var candidates = GetModelLookupCandidates(resolvedModelId);
        var fallbackMatch = pricing.FirstOrDefault(x =>
            candidates.Any(candidateModelId =>
                string.Equals(x.Key, candidateModelId, StringComparison.OrdinalIgnoreCase)
                || x.Key.EndsWith(candidateModelId, StringComparison.OrdinalIgnoreCase)
                || candidateModelId.EndsWith(x.Key, StringComparison.OrdinalIgnoreCase)));

        if (string.IsNullOrWhiteSpace(fallbackMatch.Key))
            return false;

        modelPricing = fallbackMatch.Value;
        return true;
    }

    private static IReadOnlyDictionary<string, ModelPricing> LoadLongContextPricing()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Catalog",
            "Pricing",
            "providers",
            ProviderIdentifier,
            "long_context.json");

        if (!File.Exists(path))
            return new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

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

        return perModelPricing;
    }

    private static string[] GetModelLookupCandidates(string resolvedModelId)
    {
        var trimmed = resolvedModelId.Trim();
        if (trimmed.StartsWith(ProviderIdentifier + "/", StringComparison.OrdinalIgnoreCase))
            return [trimmed, trimmed[(ProviderIdentifier.Length + 1)..]];

        return [trimmed, ProviderIdentifier + "/" + trimmed];
    }
}
