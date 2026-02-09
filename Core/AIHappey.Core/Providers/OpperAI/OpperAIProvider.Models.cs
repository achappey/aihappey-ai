using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var languageTask = ListLanguageModels(cancellationToken);
        var rerankTask = ListRerankModels(cancellationToken);

        await Task.WhenAll(languageTask, rerankTask);

        return [.. languageTask.Result, .. rerankTask.Result];
    }


    private async Task<IEnumerable<Model>> ListLanguageModels(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v2/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OpperAI API error: {await resp.Content.ReadAsStringAsync(cancellationToken)}");

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseLanguageModels(doc.RootElement);
    }

    private async Task<IEnumerable<Model>> ListRerankModels(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v2/rerank/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OpperAI API error (rerank): {await resp.Content.ReadAsStringAsync(cancellationToken)}");

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

            if (el.TryGetProperty("name", out var nameEl))
            {
                var name = nameEl.GetString();
                model.Id = name?.ToModelId(GetIdentifier()) ?? "";
                model.Name = name ?? "";
            }

            if (el.TryGetProperty("hosting_provider", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            // rerank pricing is per request → ignore for now
            // keeps pricing model consistent across providers

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }

    private IEnumerable<Model> ParseLanguageModels(JsonElement root)
    {
        var models = new List<Model>();

        var arr = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            Model model = new();

            if (el.TryGetProperty("name", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (el.TryGetProperty("hosting_provider", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("input_cost_per_token", out var inEl) &&
                el.TryGetProperty("output_cost_per_token", out var outEl))
            {
                decimal? input = inEl.ValueKind == JsonValueKind.Number ? inEl.GetDecimal() : null;
                decimal? output = outEl.ValueKind == JsonValueKind.Number ? outEl.GetDecimal() : null;

                if (input > 0 && output > 0)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = input.Value * 1_000_000m,
                        Output = output.Value * 1_000_000m
                    };
                }
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }

}
