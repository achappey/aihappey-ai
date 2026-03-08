using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LitAI;

public partial class LitAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LitAI API error: {err}");
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
                        var id = idEl.GetString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            model.Id = id.ToModelId(GetIdentifier());
                            model.Name = id;
                        }
                    }

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("description", out var descEl))
                        model.Description = descEl.GetString();

                    if (el.TryGetProperty("context_length", out var contextEl) &&
                        contextEl.TryGetInt32(out var ctx))
                        model.ContextWindow = ctx;

                    if (el.TryGetProperty("max_tokens", out var maxEl) &&
                        maxEl.TryGetInt32(out var max))
                        model.MaxTokens = max;

                    if (el.TryGetProperty("provider", out var providerEl) &&
                        providerEl.ValueKind == JsonValueKind.Object &&
                        providerEl.TryGetProperty("name", out var provNameEl))
                    {
                        model.OwnedBy = provNameEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;

                        if (pricingEl.TryGetProperty("input_cost_per_million_tokens", out var inEl) &&
                            inEl.TryGetDecimal(out var inVal))
                        {
                            input = inVal / 1_000_000m;
                        }

                        if (pricingEl.TryGetProperty("output_cost_per_million_tokens", out var outEl) &&
                            outEl.TryGetDecimal(out var outVal))
                        {
                            output = outVal / 1_000_000m;
                        }

                        if (input.HasValue && output.HasValue && input > 0 && output > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value,
                                Output = output.Value
                            };
                        }
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}