using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Synthetic;

public partial class SyntheticProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Synthetic API error: {err}");
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
                        var id = idEl.GetString();
                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = id ?? "";
                    }

                    if (el.TryGetProperty("context_length", out var ctxEl) &&
                        ctxEl.ValueKind == JsonValueKind.Number)
                        model.ContextWindow = ctxEl.GetInt32();

                    if (el.TryGetProperty("max_output_length", out var outputEl) &&
                        ctxEl.ValueKind == JsonValueKind.Number)
                        model.MaxTokens = outputEl.GetInt32();

                    if (el.TryGetProperty("hugging_face_id", out var ownerEl))
                        model.OwnedBy = ownerEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;

                        if (pricingEl.TryGetProperty("prompt", out var inEl))
                        {
                            var s = inEl.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                                input = decimal.Parse(s.Replace("$", ""), CultureInfo.InvariantCulture);
                        }

                        if (pricingEl.TryGetProperty("completion", out var outEl))
                        {
                            var s = outEl.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                                output = decimal.Parse(s.Replace("$", ""), CultureInfo.InvariantCulture);
                        }

                        if (input.HasValue && output.HasValue &&
                            input.Value > 0 && output.Value > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value,
                                Output = output.Value
                            };
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(model.Id))
                        models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}