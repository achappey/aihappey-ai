using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Darkbloom;

public partial class DarkbloomProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Darkbloom API error: {err}");
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

                    model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    model.MaxTokens = el.TryGetProperty("max_output_length", out var m) &&
                                m.ValueKind == JsonValueKind.Number
                                    ? m.GetInt32()
                                    : null;

                    model.Created = el.TryGetProperty("created", out var c) &&
                        c.ValueKind == JsonValueKind.Number
                            ? c.GetInt64()
                            : null;

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                            pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var inputPrice = pricingEl.TryGetProperty("prompt", out var promptEl)
                            ? promptEl.GetString()
                            : null;

                        var outputPrice = pricingEl.TryGetProperty("completion", out var completionEl)
                            ? completionEl.GetString()
                            : null;

                        var inputCacheReadPrice = pricingEl.TryGetProperty("input_cache_read", out var cacheReadEl)
                            ? cacheReadEl.GetString()
                            : null;

                        if (decimal.TryParse(
                                inputPrice,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var input) &&
                            decimal.TryParse(
                                outputPrice,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var output) &&
                            input != 0 &&
                            output != 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input,
                                Output = output,
                                InputCacheRead = decimal.TryParse(
                                    inputCacheReadPrice,
                                    NumberStyles.Float,
                                    CultureInfo.InvariantCulture,
                                    out var inputCacheRead)
                                        ? inputCacheRead
                                        : 0
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