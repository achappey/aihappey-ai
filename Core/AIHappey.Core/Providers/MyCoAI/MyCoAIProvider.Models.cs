using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.MyCoAI;

public partial class MyCoAIProvider
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
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"MyCoAI API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("display_name", out var displayNameEl))
                    {
                        model.Name = displayNameEl.GetString() ?? model.Name;
                    }

                    model.ContextWindow =
                        el.TryGetProperty("context_window", out var contextEl) &&
                        contextEl.ValueKind == JsonValueKind.Number
                            ? contextEl.GetInt32()
                            : null;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                    {
                        model.OwnedBy = orgEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? ParsePricing(string propertyName)
                        {
                            if (!pricingEl.TryGetProperty(propertyName, out var p))
                                return null;

                            return decimal.TryParse(
                                p.GetString(),
                                NumberStyles.Any,
                                CultureInfo.InvariantCulture,
                                out var value)
                                ? value
                                : null;
                        }

                        var input = ParsePricing("input_per_1m");
                        var output = ParsePricing("output_per_1m");
                        var cacheRead = ParsePricing("cache_read_per_1m");
                        var cacheWrite = ParsePricing("cache_write_per_1m");

                        if (input.HasValue && output.HasValue)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value,
                                Output = output.Value,
                                InputCacheRead = cacheRead,
                                InputCacheWrite = cacheWrite
                            };
                        }
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                    {
                        models.Add(model);
                    }
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}