using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<List<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var req = new HttpRequestMessage(HttpMethod.Get, "v3/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"OpperAI API error: {await resp.Content.ReadAsStringAsync(ct)}");

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                return [..ParseModels(doc.RootElement)];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private IEnumerable<Model> ParseModels(JsonElement root)
    {
        var models = new List<Model>();

        if (!root.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in arr.EnumerateArray())
        {
            Model model = new();

            if (el.TryGetProperty("id", out var idEl))
            {
                var id = idEl.GetString();
                model.Id = id?.ToModelId(GetIdentifier()) ?? "";
            }

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? "";

            if (el.TryGetProperty("provider_display_name", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            // type: rerank
            if (el.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();

                if (string.Equals(type, "rerank", StringComparison.OrdinalIgnoreCase))
                    model.Type = "reranking";
            }

            // pricing
            if (el.TryGetProperty("pricing", out var pricingEl))
            {
                decimal? input = null;
                decimal? output = null;

                if (pricingEl.TryGetProperty("input", out var inEl) &&
                    inEl.ValueKind == JsonValueKind.Array &&
                    inEl.GetArrayLength() > 0 &&
                    inEl[0].ValueKind == JsonValueKind.Number)
                {
                    input = inEl[0].GetDecimal();
                }

                if (pricingEl.TryGetProperty("output", out var outEl) &&
                    outEl.ValueKind == JsonValueKind.Array &&
                    outEl.GetArrayLength() > 0 &&
                    outEl[0].ValueKind == JsonValueKind.Number)
                {
                    output = outEl[0].GetDecimal();
                }

                if (input > 0 || output > 0)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = input ?? 0,
                        Output = output ?? 0
                    };
                }
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}
