using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Poe;

public partial class PoeProvider
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
                    throw new Exception($"Poe API error: {err}");
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

                    // ---- id ----
                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = id ?? "";
                    }

                    // ---- owner ----
                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? string.Empty;

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? string.Empty;

                    // ---- context window ----
                    if (el.TryGetProperty("context_length", out var ctxEl) &&
                        ctxEl.ValueKind == JsonValueKind.Number)
                    {
                        model.ContextWindow = ctxEl.GetInt32();
                    }
                    else if (el.TryGetProperty("context_window", out var ctxObj) &&
                             ctxObj.ValueKind == JsonValueKind.Object &&
                             ctxObj.TryGetProperty("context_length", out var nestedCtx) &&
                             nestedCtx.ValueKind == JsonValueKind.Number)
                    {
                        model.ContextWindow = nestedCtx.GetInt32();
                    }

                    // ---- pricing ----
                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;

                        if (pricingEl.TryGetProperty("prompt", out var promptEl) &&
                            promptEl.ValueKind == JsonValueKind.String)
                        {
                            if (decimal.TryParse(promptEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                                input = val;
                        }

                        if (pricingEl.TryGetProperty("completion", out var compEl) &&
                            compEl.ValueKind == JsonValueKind.String)
                        {
                            if (decimal.TryParse(compEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                                output = val;
                        }

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