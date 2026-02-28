using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
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
            throw new Exception($"Infron API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in dataEl.EnumerateArray())
        {
            var model = new Model
            {
                Tags = new List<string>()
            };

            // ID + Name
            if (el.TryGetProperty("id", out var idEl))
            {
                var rawId = idEl.GetString();
                model.Id = rawId?.ToModelId(GetIdentifier()) ?? "";
                model.Name = rawId ?? "";
            }

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString();

            if (el.TryGetProperty("display_name", out var displayName))
                model.Name = displayName.GetString() ?? model.Id;

            // Context
            if (el.TryGetProperty("context_length", out var ctxEl) &&
                ctxEl.ValueKind == JsonValueKind.Number)
            {
                model.ContextWindow = ctxEl.GetInt32();
            }

            // Pricing (preferred: min_prompt_price / min_completion_price)
            decimal? input = null;
            decimal? output = null;

            if (el.TryGetProperty("min_prompt_price", out var minInEl) &&
                minInEl.ValueKind == JsonValueKind.Number)
            {
                input = minInEl.GetDecimal();
            }

            if (el.TryGetProperty("min_completion_price", out var minOutEl) &&
                minOutEl.ValueKind == JsonValueKind.Number)
            {
                output = minOutEl.GetDecimal();
            }

            // Fallback: providers[0]
            if ((!input.HasValue || !output.HasValue) &&
                el.TryGetProperty("providers", out var providersEl) &&
                providersEl.ValueKind == JsonValueKind.Array &&
                providersEl.GetArrayLength() > 0)
            {
                var firstProvider = providersEl[0];

                if (!input.HasValue &&
                    firstProvider.TryGetProperty("prompt_price", out var pIn) &&
                    pIn.ValueKind == JsonValueKind.Number)
                {
                    input = pIn.GetDecimal();
                }

                if (!output.HasValue &&
                    firstProvider.TryGetProperty("completion_price", out var pOut) &&
                    pOut.ValueKind == JsonValueKind.Number)
                {
                    output = pOut.GetDecimal();
                }
            }

            if (input.HasValue && output.HasValue)
            {
                model.Pricing = new ModelPricing
                {
                    Input = input.Value,
                    Output = output.Value
                };
            }

            List<string> tags = [];
            // Extract supports_* flags → Tags
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name.StartsWith("supports_", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.True)
                {
                    var tag = prop.Name.Replace("supports_", "");
                    tags.Add(tag);
                }
            }

            if (tags.Count > 0)
                model.Tags = tags;

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}