using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.BergetAI;

public partial class BergetAIProvider
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
            throw new Exception($"BergetAI API error: {err}");
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
            {
                model.Name = nameEl.GetString() ?? model.Name;
            }

            if (el.TryGetProperty("context_length", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("created", out var createdEl) &&
                 createdEl.ValueKind == JsonValueKind.Number)
            {
                var ms = createdEl.GetInt64();
                model.Created = DateTimeOffset
                    .FromUnixTimeMilliseconds(ms)
                    .ToUnixTimeSeconds();
            }

            if (el.TryGetProperty("capabilities", out var capsEl) &&
                capsEl.ValueKind == JsonValueKind.Object)
            {
                var tags = new List<string>();

                foreach (var prop in capsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.True)
                    {
                        tags.Add(prop.Name);
                    }
                }

                if (tags.Count > 0)
                    model.Tags = tags.ToArray();
            }


            if (el.TryGetProperty("pricing", out var pricingEl) &&
        pricingEl.ValueKind == JsonValueKind.Object)
            {
                decimal inputNormalized = 0m;
                decimal outputNormalized = 0m;

                var unit = pricingEl.TryGetProperty("unit", out var unitEl)
                    ? unitEl.GetString()
                    : null;

                // ---- INPUT ----
                if (pricingEl.TryGetProperty("input", out var inEl))
                {
                    var input = inEl.GetDecimal();

                    if (!string.IsNullOrEmpty(unit) &&
                        unit.Contains("M Token", StringComparison.OrdinalIgnoreCase))
                    {
                        inputNormalized =
                            input.PerMillionToPerToken();
                    }
                    else
                    {
                        inputNormalized = input;
                    }
                }

                // ---- OUTPUT ----
                if (pricingEl.TryGetProperty("output", out var outEl))
                {
                    var output = outEl.GetDecimal();

                    if (!string.IsNullOrEmpty(unit) &&
                        unit.Contains("M Token", StringComparison.OrdinalIgnoreCase))
                    {
                        outputNormalized =
                            output.PerMillionToPerToken();
                    }
                    else if (!string.IsNullOrEmpty(unit) &&
                             unit.Contains("/ hour", StringComparison.OrdinalIgnoreCase))
                    {
                        outputNormalized =
                            output.PerHourToPerSecond();
                    }
                    else
                    {
                        outputNormalized = output;
                    }
                }

                if (inputNormalized > 0m || outputNormalized > 0m)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = inputNormalized,
                        Output = outputNormalized
                    };
                }
            }



            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}