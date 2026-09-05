using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.HiNow;

public partial class HiNowProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"HiNow API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

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

                    model.ContextWindow = el.TryGetProperty("context_window", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    model.MaxTokens = el.TryGetProperty("max_output_tokens", out var m) &&
                        m.ValueKind == JsonValueKind.Number
                            ? m.GetInt32()
                            : null;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        model.Name = nameEl.GetString() ?? model.Name;
                    if (el.TryGetProperty("description", out var descriptionEl) && descriptionEl.ValueKind == JsonValueKind.String)
                        model.Description = descriptionEl.GetString();
                    if (el.TryGetProperty("type", out var modelTypeEl) && modelTypeEl.ValueKind == JsonValueKind.String)
                        model.Type = modelTypeEl.GetString() ?? model.Type;
                    else if (el.TryGetProperty("endpoint", out var endpointEl) && endpointEl.ValueKind == JsonValueKind.String)
                        model.Type = endpointEl.GetString() ?? model.Type;

                    if (el.TryGetProperty("cost", out var costEl) &&
                        costEl.ValueKind == JsonValueKind.Object)
                    {
                        var type = costEl.TryGetProperty("type", out var typeEl)
                            ? typeEl.GetString()
                            : null;

                        var inputPrice = costEl.TryGetProperty("input", out var inputEl)
                            ? inputEl.GetDecimal()
                            : 0;

                        var outputPrice = costEl.TryGetProperty("output", out var outputEl)
                            ? outputEl.GetDecimal()
                            : 0;

                        if (inputPrice > 0 && outputPrice > 0)
                        {
                            var divisor = type == "mtoken" ? 1_000_000m : 1m;

                            model.Pricing = new ModelPricing
                            {
                                Input = inputPrice / divisor,
                                Output = outputPrice / divisor
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
