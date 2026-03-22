using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.NONKYCAI;

public partial class NONKYCAIProvider
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
                    throw new Exception($"NONKYCAI API error: {err}");
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

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;
                        decimal? cacheRead = null;
                        decimal? cacheWrite = null;

                        if (pricingEl.TryGetProperty("prompt", out var promptEl) &&
                            promptEl.ValueKind == JsonValueKind.Number)
                        {
                            input = promptEl.GetDecimal();
                        }

                        if (pricingEl.TryGetProperty("completion", out var completionEl) &&
                            completionEl.ValueKind == JsonValueKind.Number)
                        {
                            output = completionEl.GetDecimal();
                        }

                        if (pricingEl.TryGetProperty("input_cache_read", out var readEl) &&
                            readEl.ValueKind == JsonValueKind.Number)
                        {
                            cacheRead = readEl.GetDecimal();
                        }

                        if (pricingEl.TryGetProperty("input_cache_write", out var writeEl) &&
                            writeEl.ValueKind == JsonValueKind.Number)
                        {
                            cacheWrite = writeEl.GetDecimal();
                        }

                        if (input.HasValue && output.HasValue &&
                            input.Value > 0 && output.Value > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value,
                                Output = output.Value,
                                InputCacheRead = cacheRead,
                                InputCacheWrite = cacheWrite
                            };
                        }
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