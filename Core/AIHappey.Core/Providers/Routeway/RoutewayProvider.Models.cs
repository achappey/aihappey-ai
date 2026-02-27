using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Routeway;

public partial class RoutewayProvider
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
            throw new Exception($"Routeway API error: {err}");
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

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("short_name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? string.Empty;

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                    pricingEl.ValueKind == JsonValueKind.Object)
            {
                decimal? inputPrice = null;
                decimal? outputPrice = null;

                if (pricingEl.TryGetProperty("input", out var inEl) &&
                    inEl.ValueKind == JsonValueKind.Object &&
                    inEl.TryGetProperty("price_per_token_usd", out var inPriceEl))
                {
                    if (inPriceEl.ValueKind == JsonValueKind.String)
                        inputPrice = decimal.Parse(inPriceEl.GetString()!, CultureInfo.InvariantCulture);
                    else if (inPriceEl.ValueKind == JsonValueKind.Number)
                        inputPrice = inPriceEl.GetDecimal();
                }

                if (pricingEl.TryGetProperty("output", out var outEl) &&
                    outEl.ValueKind == JsonValueKind.Object &&
                    outEl.TryGetProperty("price_per_token_usd", out var outPriceEl))
                {
                    if (outPriceEl.ValueKind == JsonValueKind.String)
                        outputPrice = decimal.Parse(outPriceEl.GetString()!, CultureInfo.InvariantCulture);
                    else if (outPriceEl.ValueKind == JsonValueKind.Number)
                        outputPrice = outPriceEl.GetDecimal();
                }

                if (inputPrice.HasValue && outputPrice.HasValue &&
                    inputPrice.Value > 0 && outputPrice.Value > 0)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = inputPrice.Value,
                        Output = outputPrice.Value
                    };
                }
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}