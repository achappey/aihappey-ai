using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.RoutePlex;

public partial class RoutePlexProvider
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
                    throw new Exception($"RoutePlex API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                    return models;

                foreach (var el in dataEl.EnumerateArray())
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = id ?? "";
                    }

                    if (el.TryGetProperty("display_name", out var display))
                        model.Name = display.GetString() ?? model.Name;

                    if (el.TryGetProperty("context_window", out var ctx) &&
                        ctx.ValueKind == JsonValueKind.Number &&
                        ctx.TryGetInt32(out var ctxVal))
                    {
                        model.ContextWindow = ctxVal;
                    }

                    if (el.TryGetProperty("max_output_tokens", out var max) &&
                        max.ValueKind == JsonValueKind.Number &&
                        max.TryGetInt32(out var maxVal))
                    {
                        model.MaxTokens = maxVal;
                    }

                    // pricing
                    if (el.TryGetProperty("pricing", out var pricing))
                    {
                        if (pricing.TryGetProperty("input_per_1k", out var input) &&
                            input.ValueKind == JsonValueKind.Number)
                        {
                            model.Pricing ??= new ModelPricing();
                            model.Pricing.Input = input.GetDecimal() / 1000m;
                        }

                        if (pricing.TryGetProperty("output_per_1k", out var output) &&
                            output.ValueKind == JsonValueKind.Number)
                        {
                            model.Pricing ??= new ModelPricing();
                            model.Pricing.Output = output.GetDecimal() / 1000m;
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