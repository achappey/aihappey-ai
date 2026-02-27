using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.WAI;

public partial class WAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"WAI API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        var arr = root.TryGetProperty("data", out var dataEl) &&
                  dataEl.ValueKind == JsonValueKind.Array
            ? dataEl.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            var model = new Model();

            // id + name
            if (el.TryGetProperty("id", out var idEl))
            {
                var rawId = idEl.GetString();
                if (!string.IsNullOrWhiteSpace(rawId))
                {
                    model.Id = rawId.ToModelId(GetIdentifier());
                    model.Name = rawId;
                }
            }

            // description
            if (el.TryGetProperty("description", out var descEl))
                model.Description = descEl.GetString();

            // context window
            if (el.TryGetProperty("context_length", out var ctxEl) &&
                ctxEl.ValueKind == JsonValueKind.Number)
            {
                model.ContextWindow = ctxEl.GetInt32();
            }

            // max tokens
            if (el.TryGetProperty("max_output_length", out var maxEl) &&
                maxEl.ValueKind == JsonValueKind.Number)
            {
                model.MaxTokens = maxEl.GetInt32();
            }

            // Tags (supported_features + quantization)
            var tags = new List<string>();

            if (el.TryGetProperty("supported_features", out var featEl) &&
                featEl.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(
                    featEl.EnumerateArray()
                          .Select(x => x.GetString())
                          .Where(x => !string.IsNullOrWhiteSpace(x))!
                );
            }

            if (el.TryGetProperty("quantization", out var quantEl) &&
                quantEl.ValueKind == JsonValueKind.String)
            {
                tags.Add(quantEl.GetString()!);
            }

            if (tags.Count > 0)
                model.Tags = tags;

            // Pricing mapping
            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                var prompt = pricingEl.TryGetProperty("prompt", out var pEl)
                    ? pEl.GetString()
                    : null;

                var completion = pricingEl.TryGetProperty("completion", out var cEl)
                    ? cEl.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(prompt) &&
                    !string.IsNullOrWhiteSpace(completion) &&
                    prompt != "0" &&
                    completion != "0")
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = decimal.Parse(prompt, CultureInfo.InvariantCulture),
                        Output = decimal.Parse(completion, CultureInfo.InvariantCulture),
                        InputCacheRead = pricingEl.TryGetProperty("input_cache_reads", out var readEl)
                            && readEl.GetString() != "0"
                                ? decimal.Parse(readEl.GetString()!, CultureInfo.InvariantCulture)
                                : null,
                        InputCacheWrite = pricingEl.TryGetProperty("input_cache_writes", out var writeEl)
                            && writeEl.GetString() != "0"
                                ? decimal.Parse(writeEl.GetString()!, CultureInfo.InvariantCulture)
                                : null
                    };
                }
            }

            if (!string.IsNullOrWhiteSpace(model.Id))
                models.Add(model);
        }

        return models;
    }
}