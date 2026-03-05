using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ReGraph;

public partial class ReGraphProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = $"models:{GetIdentifier()}";

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"ReGraph API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr =
                    root.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array
                        ? modelsEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var rawId = idEl.GetString() ?? "";
                        model.Id = rawId.ToModelId(GetIdentifier());
                        model.Name = rawId;
                    }

                    if (el.TryGetProperty("context_length", out var ctxEl) && ctxEl.TryGetInt32(out var ctx))
                        model.ContextWindow = ctx;

                    if (el.TryGetProperty("provider", out var providerEl))
                        model.OwnedBy = providerEl.GetString() ?? "";

                    // 🔥 Pricing conversion: per 1K → per token
                    decimal? inputPerToken = null;
                    decimal? outputPerToken = null;

                    if (el.TryGetProperty("price_per_1k_tokens", out var inputEl)
                        && inputEl.TryGetDecimal(out var inputPer1k))
                    {
                        inputPerToken = inputPer1k / 1000m;
                    }

                    if (el.TryGetProperty("price_per_1k_output_tokens", out var outputEl)
                        && outputEl.TryGetDecimal(out var outputPer1k))
                    {
                        outputPerToken = outputPer1k / 1000m;
                    }

                    // if no explicit output price → assume same as input
                    if (inputPerToken.HasValue && !outputPerToken.HasValue)
                        outputPerToken = inputPerToken;

                    if (inputPerToken.HasValue && outputPerToken.HasValue
                        && inputPerToken.Value > 0 && outputPerToken.Value > 0)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = inputPerToken.Value,
                            Output = outputPerToken.Value
                        };
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                return models;

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}