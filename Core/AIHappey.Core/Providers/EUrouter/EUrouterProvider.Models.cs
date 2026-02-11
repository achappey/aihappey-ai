using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
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
            throw new Exception($"EUrouter API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in dataEl.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var modelId = idEl.GetString();
            if (string.IsNullOrEmpty(modelId))
                continue;

            string name = modelId;

            if (el.TryGetProperty("name", out var nameEl))
                name = nameEl.GetString() ?? name;

            int? contextLength = null;
            if (el.TryGetProperty("context_length", out var ctxEl) &&
                ctxEl.ValueKind == JsonValueKind.Number)
            {
                contextLength = ctxEl.GetInt32();
            }

            decimal? inputPrice = null;
            decimal? outputPrice = null;

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                if (pricingEl.TryGetProperty("prompt", out var inEl))
                {
                    if (decimal.TryParse(inEl.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                        inputPrice = parsed;
                }

                if (pricingEl.TryGetProperty("completion", out var outEl))
                {
                    if (decimal.TryParse(outEl.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                        outputPrice = parsed;
                }
            }

            // IMPORTANT PART:
            // one EUrouter model can have multiple providers
            if (el.TryGetProperty("providers", out var providersEl) &&
                providersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var providerEl in providersEl.EnumerateArray())
                {
                    if (!providerEl.TryGetProperty("slug", out var slugEl))
                        continue;

                    var providerSlug = slugEl.GetString();
                    if (string.IsNullOrEmpty(providerSlug))
                        continue;

                    var model = new Model
                    {
                        Id = $"eurouter/{providerSlug}/{modelId}",
                        Name = name,
                        OwnedBy = providerSlug,
                        ContextWindow = contextLength,
                    };

                    if (inputPrice.HasValue && outputPrice.HasValue &&
                        inputPrice.Value > 0 && outputPrice.Value > 0)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = inputPrice.Value,
                            Output = outputPrice.Value
                        };
                    }

                    models.Add(model);
                }
            }
        }

        return models;
    }
}
