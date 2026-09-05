using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Vikasit;

public partial class VikasitProvider
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
                    throw new Exception($"Vikasit API error: {err}");
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

                    if (el.TryGetProperty("limit", out var limitEl) &&
                            limitEl.ValueKind == JsonValueKind.Object)
                    {
                        model.ContextWindow = limitEl.TryGetProperty("context", out var contextEl) &&
                                              contextEl.ValueKind == JsonValueKind.Number
                            ? contextEl.GetInt32()
                            : null;

                        model.MaxTokens = limitEl.TryGetProperty("output", out var outputEl) &&
                                          outputEl.ValueKind == JsonValueKind.Number
                            ? outputEl.GetInt32()
                            : null;
                    }

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("cost", out var costEl) &&
                        costEl.ValueKind == JsonValueKind.Object)
                    {
                        var inputPrice = costEl.TryGetProperty("input", out var inputEl) &&
                                         inputEl.ValueKind == JsonValueKind.Number
                            ? inputEl.GetDecimal() / 1_000_000m
                            : 0m;

                        var outputPrice = costEl.TryGetProperty("output", out var outputEl) &&
                                          outputEl.ValueKind == JsonValueKind.Number
                            ? outputEl.GetDecimal() / 1_000_000m
                            : 0m;

                        var cacheReadPrice = costEl.TryGetProperty("cacheRead", out var cacheReadEl) &&
                                             cacheReadEl.ValueKind == JsonValueKind.Number
                            ? cacheReadEl.GetDecimal() / 1_000_000m
                            : 0m;

                        if (inputPrice > 0 && outputPrice > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = inputPrice,
                                Output = outputPrice,
                                InputCacheRead = cacheReadPrice
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