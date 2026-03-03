using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Aether;

public partial class AetherProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Aether API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in dataEl.EnumerateArray())
        {
            var model = new Model();

            // id + name
            if (el.TryGetProperty("id", out var idEl))
            {
                var rawId = idEl.GetString();
                if (!string.IsNullOrWhiteSpace(rawId))
                {
                    model.Id = rawId.ToModelId(GetIdentifier());
                    model.Name = rawId;
                }
            }

            // owned_by
            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            // context (string in JSON!)
            if (el.TryGetProperty("context", out var contextEl))
            {
                if (contextEl.ValueKind == JsonValueKind.String &&
                    int.TryParse(contextEl.GetString(), out var ctx))
                    model.ContextWindow = ctx;
            }

            // pricing (flat structure, per 1M tokens)
            decimal? input = null;
            decimal? output = null;

            if (el.TryGetProperty("input_cost", out var inEl) &&
                inEl.ValueKind == JsonValueKind.Number)
                input = inEl.GetDecimal();

            if (el.TryGetProperty("output_cost", out var outEl) &&
                outEl.ValueKind == JsonValueKind.Number)
                output = outEl.GetDecimal();

            if (input.HasValue && output.HasValue &&
                input.Value > 0 && output.Value > 0)
            {
                model.Pricing = new ModelPricing
                {
                    // Convert from per 1M → per token
                    Input = input.Value / 1_000_000m,
                    Output = output.Value / 1_000_000m
                };
            }

            if (!string.IsNullOrWhiteSpace(model.Id))
                models.Add(model);
        }

        return models;
    }
}