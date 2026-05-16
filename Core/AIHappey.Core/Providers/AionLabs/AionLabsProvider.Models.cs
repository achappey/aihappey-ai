using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.AionLabs;

public partial class AionLabsProvider
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
                    throw new Exception($"AionLabs API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array
                        ? modelsEl.EnumerateArray()
                        : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                            ? dataEl.EnumerateArray()
                            : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString() ?? "";
                        model.Id = id.ToModelId(GetIdentifier());
                        model.Name = el.TryGetProperty("name", out var nameEl)
                            ? nameEl.GetString() ?? id
                            : id;
                    }

                    if (el.TryGetProperty("description", out var descEl))
                        model.Description = descEl.GetString();

                    model.Type = "language";

                    model.ContextWindow = el.TryGetProperty("context_length", out var contextEl) &&
                        contextEl.ValueKind == JsonValueKind.Number
                            ? contextEl.GetInt32()
                            : null;

                    model.MaxTokens = el.TryGetProperty("max_completion_tokens", out var maxEl) &&
                        maxEl.ValueKind == JsonValueKind.Number
                            ? maxEl.GetInt32()
                            : null;

                    model.OwnedBy = "AionLabs";

                    if (el.TryGetProperty("date", out var dateEl))
                    {
                        var rawDate = dateEl.GetString();

                        if (DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal, out var dto))
                        {
                            model.Created = dto.ToUnixTimeSeconds();
                        }
                    }

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var inputPrice = GetPrice(pricingEl, "prompt", "input");
                        var outputPrice = GetPrice(pricingEl, "completion", "output");
                        var cacheReadPrice = GetPrice(pricingEl, "input_cache_read");

                        if (inputPrice is > 0 && outputPrice is > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = inputPrice.Value,
                                Output = outputPrice.Value,
                                InputCacheRead = cacheReadPrice
                            };
                        }
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

    private static decimal? GetPrice(JsonElement pricingEl, params string[] names)
    {
        foreach (var name in names)
        {
            if (!pricingEl.TryGetProperty(name, out var el))
                continue;

            var raw = el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : el.GetRawText();

            if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return null;
    }
}