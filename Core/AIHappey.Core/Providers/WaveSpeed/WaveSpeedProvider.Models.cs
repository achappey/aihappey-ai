using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.WaveSpeed;

public partial class WaveSpeedProvider
{
    private async Task<IEnumerable<Model>> ListWaveSpeedModelsAsync(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");

                if (!string.IsNullOrWhiteSpace(key))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"WaveSpeed model discovery failed with status {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var data = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement
                    : document.RootElement.TryGetProperty("data", out var items) &&
                      items.ValueKind == JsonValueKind.Array
                        ? items
                        : default;

                if (data.ValueKind != JsonValueKind.Array)
                    return (IEnumerable<Model>)[];

                return data.EnumerateArray()
                    .Select(ToWaveSpeedModel)
                    .OfType<Model>()
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToWaveSpeedModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var model = new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = GetString(item, "name") ?? id,
            OwnedBy = GetString(item, "owned_by") ?? GetIdentifier(),
            Description = GetString(item, "description"),
            Type = "language",
            Pricing = GetPricing(item),
        };

        if (item.TryGetProperty("created", out var created) &&
            created.ValueKind == JsonValueKind.Number &&
            created.TryGetInt64(out var timestamp))
        {
            model.Created = timestamp;
        }

        if (item.TryGetProperty("context_window", out var contextWindow) &&
            contextWindow.ValueKind == JsonValueKind.Number &&
            contextWindow.TryGetInt32(out var contextWindowValue))
        {
            model.ContextWindow = contextWindowValue;
        }

        if (item.TryGetProperty("max_tokens", out var maxTokens) &&
            maxTokens.ValueKind == JsonValueKind.Number &&
            maxTokens.TryGetInt32(out var maxTokensValue))
        {
            model.MaxTokens = maxTokensValue;
        }

        return model;
    }

    private static ModelPricing? GetPricing(JsonElement model)
    {
        if (!model.TryGetProperty("pricing", out var pricing) ||
            pricing.ValueKind != JsonValueKind.Object ||
            !HasUsdCurrency(model, pricing))
        {
            return null;
        }

        var unit = GetString(pricing, "unit");
        var pricesPerMillion = string.Equals(unit, "per_1m_tokens", StringComparison.OrdinalIgnoreCase);
        if (unit is not null && !pricesPerMillion)
            return null;

        var input = GetPrice(pricing, "input_per_1m_tokens", pricesPerMillion, "input");
        var output = GetPrice(pricing, "output_per_1m_tokens", pricesPerMillion, "output");
        if (input is null || output is null)
            return null;

        return new ModelPricing
        {
            Input = input.Value,
            Output = output.Value,
            InputCacheRead = GetPrice(pricing, "input_cache_read_per_1m_tokens", pricesPerMillion, "input_cache_read"),
            InputCacheWrite = GetPrice(pricing, "input_cache_write_per_1m_tokens", pricesPerMillion, "input_cache_write")
        };
    }

    private static bool HasUsdCurrency(JsonElement model, JsonElement pricing)
    {
        var currency = GetString(pricing, "currency") ?? GetString(model, "currency");
        return currency is null || string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? GetPrice(JsonElement pricing, string perMillionName, bool pricesPerMillion, string standardName)
    {
        if (TryGetNonNegativeDecimal(pricing, perMillionName, out var perMillion))
            return perMillion / 1_000_000m;

        if (!pricesPerMillion || !TryGetNonNegativeDecimal(pricing, standardName, out var price))
            return null;

        return price / 1_000_000m;
    }

    private static bool TryGetNonNegativeDecimal(JsonElement item, string propertyName, out decimal value)
    {
        value = default;
        return item.TryGetProperty(propertyName, out var price) &&
               price.ValueKind == JsonValueKind.Number &&
               price.TryGetDecimal(out value) &&
               value >= 0;
    }

    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "object",
        "created",
        "owned_by",
        "name",
        "description",
        "context_window",
        "max_tokens",
        "type",
        "tags",
        "pricing"
    };

    private static string? GetString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
