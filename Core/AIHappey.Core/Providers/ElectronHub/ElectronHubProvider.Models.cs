using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ElectronHub;

public partial class ElectronHubProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = $"models:{GetIdentifier()}";

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"ElectronHub API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array)
                    return models;

                foreach (var el in dataEl.EnumerateArray())
                {
                    var model = new Model();

                    // ---- id / name ----
                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var rawId = idEl.GetString() ?? "";
                        model.Id = rawId.ToModelId(GetIdentifier());
                        model.Name = rawId;
                    }

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    // ---- description ----
                    if (el.TryGetProperty("description", out var descEl))
                        model.Description = descEl.GetString() ?? "";

                    // ---- context window ----
                    if (el.TryGetProperty("tokens", out var tokensEl) &&
                        tokensEl.ValueKind == JsonValueKind.Number &&
                        tokensEl.TryGetInt32(out var ctx))
                        model.ContextWindow = ctx;

                    // ---- owned_by ----
                    if (el.TryGetProperty("owned_by", out var ownedEl))
                        model.OwnedBy = ownedEl.GetString() ?? "";

                    // ---- pricing ----
                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;

                        if (pricingEl.TryGetProperty("input", out var inEl) &&
                            inEl.ValueKind == JsonValueKind.Number)
                            input = inEl.GetDecimal();

                        if (pricingEl.TryGetProperty("output", out var outEl) &&
                            outEl.ValueKind == JsonValueKind.Number)
                            output = outEl.GetDecimal();

                        if (input.HasValue && output.HasValue &&
                            input.Value > 0 && output.Value > 0)
                        {
                            var multiplier = 1m;

                            if (pricingEl.TryGetProperty("type", out var typeEl) &&
                                typeEl.ValueKind == JsonValueKind.String)
                            {
                                var type = typeEl.GetString();

                                multiplier = type switch
                                {
                                    "per_token" => 1m,
                                    "per_thousand_tokens" => 1_000m,
                                    "per_million_tokens" => 1_000_000m,
                                    _ => 1m
                                };
                            }

                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value / multiplier,
                                Output = output.Value / multiplier
                            };
                        }
                    }

                    // ---- metadata → tags ----
                    if (el.TryGetProperty("metadata", out var metaEl) &&
                        metaEl.ValueKind == JsonValueKind.Object)
                    {
                        var tags = new List<string>();

                        foreach (var prop in metaEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.True)
                                tags.Add(prop.Name);
                        }

                        if (tags.Count > 0)
                            model.Tags = tags;
                    }

                    if (!string.IsNullOrWhiteSpace(model.Id))
                        models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}