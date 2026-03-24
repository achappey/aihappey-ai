using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.RedPill;

public partial class RedPillProvider
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
                    throw new Exception($"RedPill API error: {err}");
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

                    if (el.TryGetProperty("context_length", out var contextLengthEl))
                        model.ContextWindow = contextLengthEl.GetInt32();

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Id;

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? string.Empty;

                    if (el.TryGetProperty("pricing", out var pricingEl))
                    {
                        if (pricingEl.ValueKind == JsonValueKind.Object)
                        {
                            var inputPrice = pricingEl.TryGetProperty("prompt", out var inEl)
                                ? inEl.GetString()
                                : null;

                            var outputPrice = pricingEl.TryGetProperty("completion", out var outEl)
                                ? outEl.GetString()
                                : null;

                            if (decimal.TryParse(inputPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var input)
                                && decimal.TryParse(outputPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var output)
                                && input > 0
                                && output > 0)
                            {
                                model.Pricing = new ModelPricing
                                {
                                    Input = input,
                                    Output = output
                                };
                            }
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