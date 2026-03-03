using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Mangaba;

public partial class MangabaProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Mangaba API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in dataEl.EnumerateArray())
        {
            if (!el.TryGetProperty("available", out var availableEl) || !availableEl.GetBoolean())
                continue;

            Model model = new();

            // id
            if (el.TryGetProperty("id", out var idEl))
            {
                var rawId = idEl.GetString() ?? "";
                model.Id = rawId.ToModelId(GetIdentifier());
            }

            // name (prefer explicit name, fallback to id)
            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? "";
            else
                model.Name = el.GetProperty("id").GetString() ?? "";

            // context
            if (el.TryGetProperty("context_length", out var ctxEl))
                model.ContextWindow = ctxEl.GetInt32();

            // owned_by
            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                var unit = pricingEl.TryGetProperty("unit", out var unitEl)
                    ? unitEl.GetString()
                    : null;

                var input = pricingEl.TryGetProperty("input", out var inEl) && inEl.ValueKind == JsonValueKind.Number
                    ? inEl.GetDecimal()
                    : 0m;

                var output = pricingEl.TryGetProperty("output", out var outEl) && outEl.ValueKind == JsonValueKind.Number
                    ? outEl.GetDecimal()
                    : 0m;

                if (input > 0 || output > 0)
                {
                    // Normalize to PER TOKEN
                    if (unit == "per_1k_tokens")
                    {
                        input /= 1000m;
                        output /= 1000m;
                    }
                    else if (unit == "per_1m_tokens")
                    {
                        input /= 1_000_000m;
                        output /= 1_000_000m;
                    }

                    model.Pricing = new ModelPricing
                    {
                        Input = input,
                        Output = output
                    };
                }
            }

            // capabilities → tags
            if (el.TryGetProperty("capabilities", out var capsEl) &&
                capsEl.ValueKind == JsonValueKind.Object)
            {
                var tags = new List<string>();

                if (capsEl.TryGetProperty("streaming", out var s) && s.GetBoolean())
                    tags.Add("streaming");

                if (capsEl.TryGetProperty("vision", out var v) && v.GetBoolean())
                    tags.Add("vision");

                if (capsEl.TryGetProperty("function_calling", out var f) && f.GetBoolean())
                    tags.Add("function-calling");

                model.Tags = tags;
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}