using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Nscale;

public partial class NscaleProvider
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
            throw new Exception($"Nscale API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        // ✅ root is already an array
        var arr = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
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

            if (IsImageMode(model.Id))
                model.Type = "image";
            else
                model.Type = model.Id.Contains("embedding", StringComparison.OrdinalIgnoreCase)
                    ? "embedding" : "language";

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                var inputPrice = pricingEl.TryGetProperty("input", out var inEl)
                        ? inEl.GetRawText() : null;

                var outputPrice = pricingEl.TryGetProperty("output", out var outEl)
                        ? outEl.GetRawText() : null;

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

    private static bool IsImageMode(string? model)
        => model?.Contains("ByteDance") == true
                    || model?.Contains("stabilityai") == true
                    || model?.Contains("black-forest-labs") == true;
}