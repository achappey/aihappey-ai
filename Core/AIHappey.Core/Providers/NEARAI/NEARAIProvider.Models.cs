using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.NEARAI;

public partial class NEARAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"NEARAI API error: {err}");
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

            if (el.TryGetProperty("context_length", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
                model.ContextWindow = ctxEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            // ---- pricing (convert from per 1M tokens → per token) ----
            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                if (pricingEl.TryGetProperty("input", out var inEl) &&
                    pricingEl.TryGetProperty("output", out var outEl) &&
                    inEl.ValueKind == JsonValueKind.Number &&
                    outEl.ValueKind == JsonValueKind.Number)
                {
                    var input = inEl.GetDecimal() / 1_000_000m;
                    var output = outEl.GetDecimal() / 1_000_000m;

                    if (input > 0 && output > 0)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = input,
                            Output = output
                        };
                    }
                }
            }

            List<string> tags = [];

            // ---- modalities → tags ----
            if (el.TryGetProperty("architecture", out var archEl) &&
                archEl.ValueKind == JsonValueKind.Object)
            {
                if (archEl.TryGetProperty("inputModalities", out var inMods) &&
                    inMods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mod in inMods.EnumerateArray())
                    {
                        var v = mod.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            tags.Add($"input:{v}");
                    }
                }

                if (archEl.TryGetProperty("outputModalities", out var outMods) &&
                    outMods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mod in outMods.EnumerateArray())
                    {
                        var v = mod.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            tags.Add($"output:{v}");
                    }
                }
            }

            if (tags.Count > 0)
                model.Tags = tags;

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}