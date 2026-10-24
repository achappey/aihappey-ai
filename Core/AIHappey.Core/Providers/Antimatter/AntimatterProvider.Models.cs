using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Antimatter;

public sealed partial class AntimatterProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync<IEnumerable<Model>>(
            this.GetCacheKey(key),
            async ct =>
            {
                ApplyAuthHeader();
                using var response = await _client.GetAsync("v1/models", ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"Antimatter model discovery failed ({(int)response.StatusCode}): {error}",
                        null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Antimatter model catalog has no data array.");

                return data.EnumerateArray().Select(ParseModel).ToArray();
            },
            baseTtl: TimeSpan.FromHours(1),
            jitterMinutes: 60,
            cancellationToken: cancellationToken);
    }

    private Model ParseModel(JsonElement item)
    {
        var id = String(item, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new JsonException("Antimatter returned a model without an id.");

        var pricing = Property(item, "pricing");
        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = String(item, "name") ?? String(item, "display_name") ?? id,
            OwnedBy = String(item, "owned_by") ?? GetIdentifier(),
            Description = String(item, "description"),
            Created = Int64(item, "created"),
            Type = "language",
            ContextWindow = Int32(item, "context_length"),
            MaxTokens = Int32(item, "max_output"),
            Pricing = pricing is null ? null : new ModelPricing
            {
                Input = ReadPrice(pricing.Value, "input"),
                Output = ReadPrice(pricing.Value, "output")
            },
            Metadata = item.EnumerateObject()
                .Where(property => property.Name is not ("id" or "object" or "owned_by"
                    or "name" or "display_name" or "description" or "created" or "context_length"
                    or "max_output"))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
    }

    private static JsonElement? Property(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Int64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) ? number : null;

    private static int? Int32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;

    private static decimal ReadPrice(JsonElement pricing, string name)
    {
        if (!pricing.TryGetProperty(name, out var value))
            throw new JsonException($"Antimatter model pricing has no {name} price.");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number / 1_000_000m;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out number))
            return number / 1_000_000m;
        throw new JsonException($"Antimatter returned an invalid {name} price.");
    }
}
