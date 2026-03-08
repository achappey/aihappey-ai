using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.DocsRouter;

public partial class DocsRouterProvider
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
                    throw new Exception($"DocsRouter API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr =
                    root.ValueKind == JsonValueKind.Array
                        ? root.EnumerateArray()
                        : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                            ? dataEl.EnumerateArray()
                            : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    var model = new Model();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = id ?? "";
                    }

                    if (el.TryGetProperty("context_length", out var ctxEl) &&
                        ctxEl.ValueKind == JsonValueKind.Number)
                    {
                        model.ContextWindow = ctxEl.GetInt32();
                    }

                    if (el.TryGetProperty("description", out var descriptionEl))
                    {
                        model.Description = descriptionEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("name", out var nameEl))
                    {
                        model.Name = nameEl.GetString() ?? model.Name;
                    }


                    if (el.TryGetProperty("owned_by", out var orgEl))
                    {
                        model.OwnedBy = orgEl.GetString() ?? "";
                    }

                    // Pricing
                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        string? inputPrice =
                            pricingEl.TryGetProperty("prompt", out var promptEl)
                                ? promptEl.GetString()
                                : pricingEl.TryGetProperty("input", out var inputEl)
                                    ? inputEl.GetString()
                                    : null;

                        string? outputPrice =
                            pricingEl.TryGetProperty("completion", out var completionEl)
                                ? completionEl.GetString()
                                : pricingEl.TryGetProperty("output", out var outputEl)
                                    ? outputEl.GetString()
                                    : null;

                        if (decimal.TryParse(inputPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var input) &&
                            decimal.TryParse(outputPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var output) &&
                            input > 0 && output > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input,
                                Output = output
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