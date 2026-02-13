using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.MatterAI;

public partial class MatterAIProvider
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
            throw new Exception($"MatterAI API error: {err}");
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

            if (el.TryGetProperty("max_output_length", out var maxOutputEl))
                model.MaxTokens = maxOutputEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? "";

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Name;

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                static decimal? ParsePrice(JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.Number &&
                        element.TryGetDecimal(out var number))
                        return number;

                    if (element.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(
                            element.GetString(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var parsed))
                        return parsed;

                    return null;
                }

                var input = pricingEl.TryGetProperty("prompt", out var inEl)
                    ? ParsePrice(inEl)
                    : null;

                var output = pricingEl.TryGetProperty("completion", out var outEl)
                    ? ParsePrice(outEl)
                    : null;

                if ((input ?? 0) > 0 || (output ?? 0) > 0)
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