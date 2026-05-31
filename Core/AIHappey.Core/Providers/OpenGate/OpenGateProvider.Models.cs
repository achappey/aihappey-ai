using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OpenGate;

public partial class OpenGateProvider
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
                    throw new Exception($"OpenGate API error: {err}");
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
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? string.Empty;
                        model.Name = idEl.GetString() ?? string.Empty;
                    }

                    model.ContextWindow = el.TryGetProperty("context_window", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? string.Empty;

                    if (el.TryGetProperty("display_name", out var displayNameEl))
                        model.Name = displayNameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("max_output", out var maxOutputEl) &&
                        maxOutputEl.ValueKind == JsonValueKind.Number)
                    {
                        model.MaxTokens = maxOutputEl.GetInt32();
                    }

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;

                        if (pricingEl.TryGetProperty("input_per_m_usd", out var inEl) &&
                            inEl.ValueKind == JsonValueKind.Number)
                        {
                            input = inEl.GetDecimal() / 1_000_000m;
                        }

                        if (pricingEl.TryGetProperty("output_per_m_usd", out var outEl) &&
                            outEl.ValueKind == JsonValueKind.Number)
                        {
                            output = outEl.GetDecimal() / 1_000_000m;
                        }

                        if (input.HasValue && output.HasValue)
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