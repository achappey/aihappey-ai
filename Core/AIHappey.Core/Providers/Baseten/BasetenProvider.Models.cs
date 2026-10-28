using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Baseten;

public partial class BasetenProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keyResolver.Resolve(GetIdentifier());

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
                    throw new Exception($"Baseten API error: {err}");
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
                    string? nativeId = null;

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        nativeId = idEl.GetString();
                        model.Id = nativeId?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = nativeId ?? "";
                    }

                    model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? string.Empty;

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? string.Empty;

                    if (nativeId is not null && BasetenDecisionModels.TryGetValue(nativeId, out var decisionModel))
                    {
                        model.Type = "decision";
                        model.Name = string.IsNullOrWhiteSpace(model.Name) ? decisionModel.Name : model.Name;
                        model.Description = string.IsNullOrWhiteSpace(model.Description) ? decisionModel.Description : model.Description;
                        model.ContextWindow ??= BasetenDecisionContextWindow;
                    }

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                         pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var inputPrice = pricingEl.TryGetProperty("input", out var inputEl)
                            ? inputEl.GetString()
                            : pricingEl.TryGetProperty("prompt", out var promptEl)
                                ? promptEl.GetString()
                                : null;

                        var outputPrice = pricingEl.TryGetProperty("output", out var outputEl)
                            ? outputEl.GetString()
                            : pricingEl.TryGetProperty("completion", out var completionEl)
                                ? completionEl.GetString()
                                : null;

                        if (decimal.TryParse(inputPrice, CultureInfo.InvariantCulture, out var input) &&
                            decimal.TryParse(outputPrice, CultureInfo.InvariantCulture, out var output) &&
                            input > 0 &&
                            output > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input,
                                Output = output
                            };
                        }
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                foreach (var (id, metadata) in BasetenDecisionModels)
                {
                    var qualifiedId = id.ToModelId(GetIdentifier());
                    if (models.Any(model => string.Equals(model.Id, qualifiedId, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    models.Add(new Model
                    {
                        Id = qualifiedId,
                        Name = metadata.Name,
                        Description = metadata.Description,
                        OwnedBy = GetIdentifier(),
                        Type = "decision",
                        ContextWindow = BasetenDecisionContextWindow
                    });
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}