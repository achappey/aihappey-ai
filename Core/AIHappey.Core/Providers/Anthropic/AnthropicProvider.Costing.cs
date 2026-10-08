using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    // Anthropic bills web search at $10 per 1,000 searches, in addition to tokens.
    private const decimal WebSearchRequestCostUsd = 0.01m;

    private static decimal? GetAnthropicGatewayCost(Dictionary<string, object?>? metadata)
    {
        var json = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
        return TryGetProperty(json, "gateway", out var gateway)
            && TryGetProperty(gateway, "cost", out var cost)
            && cost.ValueKind == JsonValueKind.Number && cost.TryGetDecimal(out var value)
                ? value : null;
    }

    private static Dictionary<string, JsonElement>? AddAnthropicChatCompletionCost(
        Dictionary<string, JsonElement>? additionalProperties,
        decimal? cost)
    {
        if (!cost.HasValue)
            return additionalProperties;

        var enriched = additionalProperties is null ? [] : new Dictionary<string, JsonElement>(additionalProperties);
        var metadata = enriched.TryGetValue("metadata", out var existing) && existing.ValueKind == JsonValueKind.Object
            ? existing.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web)
            : null;
        enriched["metadata"] = JsonSerializer.SerializeToElement(ModelCostMetadataEnricher.AddCost(metadata, cost),
            JsonSerializerOptions.Web);
        return enriched;
    }

    private async Task<decimal?> GetManagedAgentCostBaselineAsync(
        AnthropicManagedAgentSessionResolution session,
        CancellationToken cancellationToken)
    {
        if (session.Created)
            return 0m;

        var currentSession = session.RawSession
            ?? await RetrieveManagedAgentSessionAsync(session.Id, cancellationToken);
        return GetManagedAgentListCostCents(GetManagedAgentUsage(currentSession));
    }

    private static decimal? GetManagedAgentRequestCostUsd(
        JsonElement session,
        JsonElement? streamedUsage,
        decimal? baselineCents,
        bool submittedInput)
    {
        // The retrieved session is newer than the last session.usage event. Both
        // amounts are cumulative and all-in: never add token/runtime/tool costs.
        var finalCents = GetManagedAgentListCostCents(GetManagedAgentUsage(session))
            ?? GetManagedAgentListCostCents(streamedUsage);
        if (!finalCents.HasValue)
            return null;

        // Replaying persisted history did not submit new work and must not rebill it.
        if (!submittedInput)
            return 0m;

        // A missing baseline on an existing session must not turn its entire
        // historical spend into the price of the current gateway request.
        return baselineCents.HasValue
            ? Math.Max(0m, finalCents.Value - baselineCents.Value) / 100m
            : null;
    }

    private static decimal? GetManagedAgentListCostCents(JsonElement? usage)
    {
        if (!usage.HasValue
            || !TryGetProperty(usage.Value, "list_cost", out var listCost)
            || !string.Equals(TryGetString(listCost, "currency"), "USD", StringComparison.OrdinalIgnoreCase)
            || !TryGetProperty(listCost, "amount", out var amount))
            return null;

        decimal cents = 0m;
        var parsed = amount.ValueKind switch
        {
            JsonValueKind.Number => amount.TryGetDecimal(out cents),
            JsonValueKind.String => decimal.TryParse(amount.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out cents),
            _ => false
        };
        return parsed && cents >= 0m ? cents : null;
    }

    private static JsonElement? GetManagedAgentUsage(JsonElement element)
        => TryGetProperty(element, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? usage.Clone()
            : null;
}
