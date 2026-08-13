using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.NexosAI;

public partial class NexosAIProvider
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
                    throw new Exception($"NexosAI API error: {err}");
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

                    if (el.TryGetProperty("nexos_model_id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    model.MaxTokens = el.TryGetProperty("max_tokens", out var m) &&
                            m.ValueKind == JsonValueKind.Number
                                ? m.GetInt32()
                                : null;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                            pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var inputPrice = pricingEl.TryGetProperty("input_cost_per_token", out var inputEl) &&
                                         inputEl.ValueKind == JsonValueKind.String
                            ? inputEl.GetString()
                            : null;

                        var outputPrice = pricingEl.TryGetProperty("output_cost_per_token", out var outputEl) &&
                                          outputEl.ValueKind == JsonValueKind.String
                            ? outputEl.GetString()
                            : null;

                        var cacheReadPrice = pricingEl.TryGetProperty("cache_read_cost_per_token", out var cacheReadEl) &&
                                             cacheReadEl.ValueKind == JsonValueKind.String
                            ? cacheReadEl.GetString()
                            : null;

                        var cacheWritePrice = pricingEl.TryGetProperty("cache_write_cost_per_token", out var cacheWriteEl) &&
                                              cacheWriteEl.ValueKind == JsonValueKind.String
                            ? cacheWriteEl.GetString()
                            : null;

                        var cacheWrite1hPrice = pricingEl.TryGetProperty("cache_write_cost_per_token_1h_ttl", out var cacheWrite1hEl) &&
                                                cacheWrite1hEl.ValueKind == JsonValueKind.String
                            ? cacheWrite1hEl.GetString()
                            : null;

                        if (!string.IsNullOrEmpty(inputPrice) &&
                            !string.IsNullOrEmpty(outputPrice) &&
                            inputPrice != "0" &&
                            outputPrice != "0")
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = decimal.Parse(inputPrice, CultureInfo.InvariantCulture),
                                Output = decimal.Parse(outputPrice, CultureInfo.InvariantCulture),

                                InputCacheRead = !string.IsNullOrEmpty(cacheReadPrice)
                                    ? decimal.Parse(cacheReadPrice, CultureInfo.InvariantCulture)
                                    : null,

                                InputCacheWrite = !string.IsNullOrEmpty(cacheWritePrice)
                                    ? decimal.Parse(cacheWritePrice, CultureInfo.InvariantCulture)
                                    : null,
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