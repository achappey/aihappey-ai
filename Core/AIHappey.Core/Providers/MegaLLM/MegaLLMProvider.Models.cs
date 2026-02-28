using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.MegaLLM;

public partial class MegaLLMProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"MegaLLM API error: {err}");
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

            if (el.TryGetProperty("created", out var createdEl))
                model.Created = createdEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("display_name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                if (pricingEl.TryGetProperty("input_tokens_cost_per_million", out var inEl) &&
                    pricingEl.TryGetProperty("output_tokens_cost_per_million", out var outEl) &&
                    inEl.ValueKind == JsonValueKind.Number &&
                    outEl.ValueKind == JsonValueKind.Number)
                {
                    var inputPerMillion = inEl.GetDecimal();
                    var outputPerMillion = outEl.GetDecimal();

                    if (inputPerMillion > 0 && outputPerMillion > 0)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = inputPerMillion / 1_000_000m, // per token
                            Output = outputPerMillion / 1_000_000m  // per token
                        };
                    }
                }
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}