using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.EdenAI;

public partial class EdenAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v3/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"EdenAI API error: {err}");
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
                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = id ?? "";
                    }

                    if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
                        model.Created = createdEl.GetInt64();

                    if (el.TryGetProperty("description", out var descEl))
                        model.Description = descEl.GetString();

                    if (el.TryGetProperty("context_length", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
                        model.ContextWindow = ctxEl.GetInt32();

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;

                        if (pricingEl.TryGetProperty("input_cost_per_token", out var inEl) &&
                            inEl.TryGetDecimal(out var inVal))
                            input = inVal;

                        if (pricingEl.TryGetProperty("output_cost_per_token", out var outEl) &&
                            outEl.TryGetDecimal(out var outVal))
                            output = outVal;

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

                models.AddRange(GetIdentifier().GetModels());

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}