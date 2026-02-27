using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Kilo;

public partial class KiloProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Kilo API error: {err}");
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
                model.Name = idEl.GetString() ?? string.Empty;
            }

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString();

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
                model.Created = createdEl.GetInt64();

            if (el.TryGetProperty("context_length", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? string.Empty;

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                var inputPrice = pricingEl.TryGetProperty("prompt", out var inEl)
                        ? inEl.GetString() : null;

                var outputPrice = pricingEl.TryGetProperty("completion", out var outEl)
                        ? outEl.GetString() : null;

                if (!string.IsNullOrEmpty(outputPrice)
                    && !string.IsNullOrEmpty(inputPrice)
                    && !outputPrice.Equals("0")
                    && !inputPrice.Equals("0"))
                    model.Pricing = new ModelPricing
                    {
                        Input = decimal.Parse(inputPrice, CultureInfo.InvariantCulture),
                        Output = decimal.Parse(outputPrice, CultureInfo.InvariantCulture)
                    };
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}