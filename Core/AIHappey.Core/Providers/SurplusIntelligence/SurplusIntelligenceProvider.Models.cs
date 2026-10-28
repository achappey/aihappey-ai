using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SurplusIntelligence;

public sealed partial class SurplusIntelligenceProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        return await memoryCache.GetOrCreateAsync(
            "models:surplusintelligence",
            async ct =>
            {
                using var response = await _client.GetAsync("v1/models", ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"Surplus Intelligence model-listing error ({(int)response.StatusCode}): {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Surplus Intelligence model catalog must contain a data array.");

                var models = new List<Model>();
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                        continue;

                    var nativeId = idElement.GetString()!;
                    var type = ReadModelString(item, "type") ?? nativeId.GuessModelType();
                    if (IsDocumentedDecisionModel(nativeId))
                        type = "decision";

                    var model = new Model
                    {
                        Id = nativeId.ToModelId(GetIdentifier()),
                        Name = ReadModelString(item, "name") ?? nativeId,
                        OwnedBy = ReadModelString(item, "owned_by")
                            ?? ReadModelString(item, "provider")
                            ?? "Surplus Intelligence",
                        Description = ReadModelString(item, "description"),
                        Type = type,
                        ContextWindow = ReadInt32(item, "context_length"),
                        MaxTokens = ReadInt32(item, "max_completion_tokens")
                            ?? ReadNestedInt32(item, "top_provider", "max_completion_tokens"),
                        Created = ReadInt64(item, "created"),
                        Tags = ReadTags(item)
                    };

                    if (item.TryGetProperty("pricing", out var pricing))
                    {
                        if (pricing.ValueKind != JsonValueKind.Object)
                            throw new JsonException($"Surplus Intelligence pricing for '{nativeId}' must be an object.");
                        model.Pricing = new ModelPricing
                        {
                            Input = ReadPrice(pricing, "prompt"),
                            Output = ReadPrice(pricing, "completion")
                        };
                    }

                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromSeconds(60),
            jitterMinutes: 0,
            cancellationToken: cancellationToken);
    }

    private static bool IsDocumentedDecisionModel(string nativeId)
        => nativeId.Equals("gpt-6-luna", StringComparison.OrdinalIgnoreCase)
            || nativeId.StartsWith("jev-", StringComparison.OrdinalIgnoreCase)
            || nativeId.Equals("typesafe/jev", StringComparison.OrdinalIgnoreCase)
            || nativeId.StartsWith("typesafe/jev-", StringComparison.OrdinalIgnoreCase);

    private static string? ReadModelString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
                ? result
                : null;

    private static long? ReadInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result)
                ? result
                : null;

    private static int? ReadNestedInt32(JsonElement element, string parentName, string propertyName)
        => element.TryGetProperty(parentName, out var parent)
            && parent.ValueKind == JsonValueKind.Object
                ? ReadInt32(parent, propertyName)
                : null;

    private static decimal ReadPrice(JsonElement pricing, string propertyName)
    {
        if (!pricing.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || !decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
            throw new JsonException($"Surplus Intelligence pricing.{propertyName} must be a decimal string.");
        return result;
    }

    private static IEnumerable<string>? ReadTags(JsonElement item)
    {
        var tags = new List<string>();
        foreach (var propertyName in new[] { "supported_features", "supported_parameters" })
        {
            if (item.TryGetProperty(propertyName, out var values) && values.ValueKind == JsonValueKind.Array)
                tags.AddRange(values.EnumerateArray()
                    .Where(static value => value.ValueKind == JsonValueKind.String)
                    .Select(static value => value.GetString()!)
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
        }

        if (item.TryGetProperty("architecture", out var architecture)
            && architecture.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "input_modalities", "output_modalities" })
            {
                if (architecture.TryGetProperty(propertyName, out var modalities)
                    && modalities.ValueKind == JsonValueKind.Array)
                    tags.AddRange(modalities.EnumerateArray()
                        .Where(static value => value.ValueKind == JsonValueKind.String)
                        .Select(static value => value.GetString()!)
                        .Where(static value => !string.IsNullOrWhiteSpace(value)));
            }
        }

        return tags.Count == 0 ? null : tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
