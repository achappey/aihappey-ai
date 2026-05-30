using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.CallMissed;

public partial class CallMissedProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"CallMissed API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var root = doc.RootElement;
                var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                    ? dataEl.EnumerateArray()
                    : root.ValueKind == JsonValueKind.Array
                        ? root.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                var models = new List<Model>();

                foreach (var el in arr)
                {
                    var rawId = ReadModelString(el, "id");
                    if (string.IsNullOrWhiteSpace(rawId))
                        continue;

                    var model = new Model
                    {
                        Id = rawId.ToModelId(GetIdentifier()),
                        Name = ReadModelString(el, "name") ?? rawId,
                        Description = ReadModelString(el, "description"),
                        OwnedBy = ReadModelString(el, "owned_by")
                                  ?? rawId.Split('/').FirstOrDefault()
                                  ?? GetIdentifier(),
                        ContextWindow = ReadModelInt(el, "context_window") ?? ReadModelInt(el, "context_length"),
                        MaxTokens = ReadModelInt(el, "max_tokens"),
                        Type = ReadModelString(el, "type") ?? rawId.GuessModelType(),
                        Pricing = ReadPricing(el)
                    };

                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string? ReadModelString(JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static int? ReadModelInt(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static ModelPricing? ReadPricing(JsonElement modelEl)
    {
        if (!modelEl.TryGetProperty("pricing", out var pricingEl) || pricingEl.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadDecimal(pricingEl, "input") ?? ReadDecimal(pricingEl, "prompt");
        var output = ReadDecimal(pricingEl, "output") ?? ReadDecimal(pricingEl, "completion");

        if (input is null && output is null)
            return null;

        return new ModelPricing
        {
            Input = input ?? 0m,
            Output = output ?? 0m,
            InputCacheRead = ReadDecimal(pricingEl, "input_cache_read"),
            InputCacheWrite = ReadDecimal(pricingEl, "input_cache_write")
        };
    }

    private static decimal? ReadDecimal(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var number))
            return number;

        if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(
                prop.GetString(),
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
