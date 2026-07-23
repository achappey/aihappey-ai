using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CognitivessAI;

public partial class CognitivessAIProvider
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
                    throw new Exception($"CognitivessAI API error: {err}");
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

                    model.MaxTokens = el.TryGetProperty("max_output", out var m) &&
                        m.ValueKind == JsonValueKind.Number
                            ? m.GetInt32()
                            : null;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                         pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var promptPrice = pricingEl.TryGetProperty("prompt", out var promptEl) &&
                                          promptEl.TryGetDecimal(out var prompt)
                            ? prompt
                            : 0m;

                        var completionPrice = pricingEl.TryGetProperty("completion", out var completionEl) &&
                                              completionEl.TryGetDecimal(out var completion)
                            ? completion
                            : 0m;

                        var cachedPromptPrice = pricingEl.TryGetProperty("cached_prompt", out var cachedPromptEl) &&
                                                cachedPromptEl.TryGetDecimal(out var cachedPrompt)
                            ? cachedPrompt
                            : 0m;

                        // Prices are USD per 1,000,000 tokens, so convert to per-token pricing.
                        if (promptPrice > 0m && completionPrice > 0m)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = promptPrice / 1_000_000m,
                                Output = completionPrice / 1_000_000m,
                                InputCacheRead = cachedPromptPrice > 0m
                                    ? cachedPromptPrice / 1_000_000m
                                    : null
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