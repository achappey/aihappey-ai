using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.TokenFlux;

public partial class TokenFluxProvider
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
                    throw new Exception($"TokenFlux API error: {err}");
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

                    model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                         pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var pricing = new ModelPricing();

                        if (pricingEl.TryGetProperty("prompt", out var promptEl) &&
                            decimal.TryParse(promptEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var prompt))
                        {
                            pricing.Input = prompt;
                        }

                        if (pricingEl.TryGetProperty("completion", out var completionEl) &&
                            decimal.TryParse(completionEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var completion))
                        {
                            pricing.Output = completion;
                        }

                        if (pricingEl.TryGetProperty("input_cache_read", out var readEl) &&
                            decimal.TryParse(readEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var read))
                        {
                            pricing.InputCacheRead = read;
                        }

                        if (pricingEl.TryGetProperty("input_cache_write", out var writeEl) &&
                            decimal.TryParse(writeEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var write))
                        {
                            pricing.InputCacheWrite = write;
                        }

                        if (pricing.Input > 0 || pricing.Output > 0)
                        {
                            model.Pricing = pricing;
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