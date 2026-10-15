using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Models;

public static class ModelPricingCatalog
{
    public static ModelPricing? Resolve(JsonElement modelEntry, DateTimeOffset asOf)
    {
        ModelPricing? result = modelEntry.TryGetProperty("pricing", out var pricing)
            ? pricing.Deserialize<ModelPricing>(JsonSerializerOptions.Web)
            : null;

        if (!modelEntry.TryGetProperty("pricing_schedule", out var schedule)
            || schedule.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var version in schedule.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new
            {
                Entry = item,
                EffectiveAt = item.TryGetProperty("effective_at", out var effectiveAt)
                    && effectiveAt.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(
                        effectiveAt.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var parsed)
                        ? parsed
                        : (DateTimeOffset?)null
            })
            .Where(item => item.EffectiveAt.HasValue && item.EffectiveAt.Value <= asOf)
            .OrderBy(item => item.EffectiveAt))
        {
            if (version.Entry.TryGetProperty("pricing", out var versionPricing))
                result = versionPricing.Deserialize<ModelPricing>(JsonSerializerOptions.Web);
        }

        return result;
    }
}
