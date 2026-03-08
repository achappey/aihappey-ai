using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Lunos;

public partial class LunosProvider
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
                    throw new Exception($"Lunos API error: {err}");
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

                    if (el.TryGetProperty("owned_by", out var ownerEl))
                        model.OwnedBy = ownerEl.GetString() ?? "";

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? "";

                    if (el.TryGetProperty("parameters", out var paramEl) &&
                        paramEl.TryGetProperty("context", out var ctxEl))
                        model.ContextWindow = ctxEl.GetInt32();

                    if (el.TryGetProperty("parameters", out var paramEl2) &&
                       paramEl2.TryGetProperty("max_output_tokens", out var tokenEl))
                        model.MaxTokens = tokenEl.GetInt32();

                    if (el.TryGetProperty("pricePerMillionTokens", out var priceEl) &&
                        priceEl.ValueKind == JsonValueKind.Object)
                    {
                        var input = priceEl.TryGetProperty("input", out var inEl)
                            ? inEl.GetDecimal()
                            : 0;

                        var output = priceEl.TryGetProperty("output", out var outEl)
                            ? outEl.GetDecimal()
                            : 0;

                        if (input > 0 && output > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input / 1_000_000m,
                                Output = output / 1_000_000m
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