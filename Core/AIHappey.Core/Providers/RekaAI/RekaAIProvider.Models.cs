using AIHappey.Core.AI;
using System.Text.Json;
using System.Globalization;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
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
            throw new Exception($"RekaAI API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in dataEl.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var model = new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = el.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString() ?? id
                        : id,
                OwnedBy = GetIdentifier(),
                Type = "language"
            };

            // ---- description ----
            if (el.TryGetProperty("description", out var descEl))
                model.Description = descEl.GetString() ?? "";

            // ---- context window ----
            if (el.TryGetProperty("context_length", out var ctxEl) &&
                ctxEl.ValueKind == JsonValueKind.Number)
                model.ContextWindow = ctxEl.GetInt32();

            // ---- max tokens ----
            if (el.TryGetProperty("max_output_length", out var maxEl) &&
                maxEl.ValueKind == JsonValueKind.Number)
                model.MaxTokens = maxEl.GetInt32();

            // ---- pricing (strings → decimal per token) ----
            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                var inputRaw = pricingEl.TryGetProperty("prompt", out var inEl)
                    ? inEl.GetString()
                    : null;

                var outputRaw = pricingEl.TryGetProperty("completion", out var outEl)
                    ? outEl.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(inputRaw) &&
                    !string.IsNullOrWhiteSpace(outputRaw) &&
                    inputRaw != "0" &&
                    outputRaw != "0" &&
                    decimal.TryParse(inputRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var input) &&
                    decimal.TryParse(outputRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var output))
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = input,
                        Output = output
                    };
                }
            }

            var tags = new List<string>();

            // ---- capabilities ----
            if (el.TryGetProperty("supported_features", out var featuresEl) &&
                featuresEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var feat in featuresEl.EnumerateArray())
                {
                    var value = feat.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        tags.Add(value);
                }
            }

            // ---- input modalities ----
            if (el.TryGetProperty("input_modalities", out var inMods) &&
                inMods.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in inMods.EnumerateArray())
                {
                    var value = mod.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        tags.Add($"input:{value}");
                }
            }

            // ---- output modalities ----
            if (el.TryGetProperty("output_modalities", out var outMods) &&
                outMods.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in outMods.EnumerateArray())
                {
                    var value = mod.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        tags.Add($"output:{value}");
                }
            }

            // assign once
            model.Tags = tags;

            models.Add(model);
        }

        return models;
    }
}