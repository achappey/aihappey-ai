using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.RodiumAI;

public partial class RodiumAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"RodiumAI API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array)
                {
                    return models;
                }

                foreach (var el in dataEl.EnumerateArray())
                {
                    var rawId = GetString(el, "id");

                    if (string.IsNullOrWhiteSpace(rawId))
                        continue;

                    var model = new Model
                    {
                        Id = rawId.ToModelId(GetIdentifier()),
                        Name = GetString(el, "rodiumai_display_name") ?? rawId,
                        Description = GetString(el, "rodiumai_description") ?? string.Empty,
                        OwnedBy =
                            GetNestedString(el, "rodiumai_provider", "name")
                            ?? GetString(el, "owned_by")
                            ?? ""
                    };

                    if (el.TryGetProperty("rodiumai_capabilities", out var capabilitiesEl) &&
                        capabilitiesEl.ValueKind == JsonValueKind.Object)
                    {
                        model.ContextWindow = GetInt32(capabilitiesEl, "context_window");
                    }

                    if (el.TryGetProperty("rodiumai_pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var pricingUnit = GetString(pricingEl, "pricing_unit");

                        var inputPer1m = GetDecimal(pricingEl, "input_per_1m");
                        var outputPer1m = GetDecimal(pricingEl, "output_per_1m");

                        if (pricingUnit == "per_million_tokens" &&
                            inputPer1m is > 0 &&
                            outputPer1m is > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = inputPer1m.Value / 1_000_000m,
                                Output = outputPer1m.Value / 1_000_000m
                            };

                            var cachedInputPer1m = GetDecimal(pricingEl, "cached_input_per_1m");
                            if (cachedInputPer1m.HasValue && cachedInputPer1m > 0)
                                model.Pricing.InputCacheRead = cachedInputPer1m / 1_000_000m;
                        }
                    }

                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string? GetString(JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var valueEl) &&
               valueEl.ValueKind == JsonValueKind.String
            ? valueEl.GetString()
            : null;
    }

    private static string? GetNestedString(JsonElement el, string objectName, string propertyName)
    {
        if (!el.TryGetProperty(objectName, out var objectEl) ||
            objectEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(objectEl, propertyName);
    }

    private static int? GetInt32(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var valueEl))
            return null;

        return valueEl.ValueKind switch
        {
            JsonValueKind.Number when valueEl.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(valueEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static decimal? GetDecimal(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var valueEl))
            return null;

        return valueEl.ValueKind switch
        {
            JsonValueKind.Number when valueEl.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(valueEl.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }
}