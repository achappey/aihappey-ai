using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.ArceeAI;

public partial class ArceeAIProvider
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
            throw new Exception($"ArceeAI API error: {err}");
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

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? string.Empty;

            if (el.TryGetProperty("context_length", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("max_output_length", out var maxOutputEl))
                model.MaxTokens = maxOutputEl.GetInt32();

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                    pricingEl.ValueKind == JsonValueKind.Object)
            {
                decimal? input = null;
                decimal? output = null;

                if (pricingEl.TryGetProperty("prompt", out var inEl))
                {
                    input = inEl.ValueKind == JsonValueKind.String
                        ? decimal.Parse(inEl.GetString()!, CultureInfo.InvariantCulture)
                        : inEl.GetDecimal();
                }

                if (pricingEl.TryGetProperty("completion", out var outEl))
                {
                    output = outEl.ValueKind == JsonValueKind.String
                        ? decimal.Parse(outEl.GetString()!, CultureInfo.InvariantCulture)
                        : outEl.GetDecimal();
                }

                if (input.HasValue && output.HasValue && input != 0 && output != 0)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = input.Value,
                        Output = output.Value
                    };
                }
            }
            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}