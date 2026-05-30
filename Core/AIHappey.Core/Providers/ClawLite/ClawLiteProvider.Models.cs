using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;
using System.Globalization;

namespace AIHappey.Core.Providers.ClawLite;

public partial class ClawLiteProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"ClawLite API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.ValueKind == JsonValueKind.Object &&
                          root.TryGetProperty("models", out var modelsEl) &&
                          modelsEl.ValueKind == JsonValueKind.Array
                    ? modelsEl.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    var model = new Model();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();

                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = el.TryGetProperty("name", out var nameEl)
                            ? nameEl.GetString() ?? id ?? ""
                            : id ?? "";
                    }

                    if (el.TryGetProperty("provider", out var providerEl))
                        model.OwnedBy = providerEl.GetString() ?? "";

                    model.ContextWindow = el.TryGetProperty("contextWindow", out var ctxEl) &&
                                          ctxEl.ValueKind == JsonValueKind.Number
                        ? ctxEl.GetInt32()
                        : null;

                    if (TryGetDecimal(el, "inputPer1M", out var inputPer1M) &&
                        TryGetDecimal(el, "outputPer1M", out var outputPer1M) &&
                        inputPer1M > 0 &&
                        outputPer1M > 0)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = inputPer1M / 1_000_000m,
                            Output = outputPer1M / 1_000_000m
                        };
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

    private static bool TryGetDecimal(JsonElement el, string propertyName, out decimal value)
    {
        value = 0;

        if (!el.TryGetProperty(propertyName, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }
}