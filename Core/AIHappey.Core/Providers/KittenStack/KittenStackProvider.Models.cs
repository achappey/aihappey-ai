using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.KittenStack;

public partial class KittenStackProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"KittenStack API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr =
                    root.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array
                        ? modelsEl.EnumerateArray()
                        : root.ValueKind == JsonValueKind.Array
                            ? root.EnumerateArray()
                            : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = id ?? "";
                    }

                    if (el.TryGetProperty("display_name", out var displayEl))
                        model.Name = displayEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("description", out var descEl))
                        model.Description = descEl.GetString();

                    if (el.TryGetProperty("context_window", out var ctxEl))
                        model.ContextWindow = ctxEl.GetInt32();
                    else if (el.TryGetProperty("context_size", out var ctxSizeEl))
                        model.ContextWindow = ctxSizeEl.GetInt32();

                    if (el.TryGetProperty("provider", out var providerEl))
                        model.OwnedBy = providerEl.GetString() ?? "";

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;

                        if (pricingEl.TryGetProperty("prompt", out var inEl) && inEl.ValueKind == JsonValueKind.Number)
                            input = inEl.GetDecimal();

                        if (pricingEl.TryGetProperty("completion", out var outEl) && outEl.ValueKind == JsonValueKind.Number)
                            output = outEl.GetDecimal();

                        if (input.HasValue && output.HasValue && input.Value > 0 && output.Value > 0)
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