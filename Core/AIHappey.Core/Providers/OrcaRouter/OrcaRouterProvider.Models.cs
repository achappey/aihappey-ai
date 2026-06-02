using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.OrcaRouter;

public partial class OrcaRouterProvider
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
                    throw new Exception($"OrcaRouter API error: {err}");
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
                        var id = idEl.GetString() ?? "";
                        model.Id = id.ToModelId(GetIdentifier());
                        model.Name = id;
                    }

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("description", out var descEl))
                        model.Description = descEl.GetString() ?? "";

                    model.ContextWindow = GetInt(el, "context_length");
                    model.MaxTokens = GetInt(el, "max_completion_tokens");

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        var input = GetDecimal(pricingEl, "prompt");
                        var output = GetDecimal(pricingEl, "completion");

                        if (input is > 0 && output is > 0)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value,
                                Output = output.Value
                            };
                        }
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                static int? GetInt(JsonElement el, string propertyName)
                {
                    return el.TryGetProperty(propertyName, out var v) &&
                           v.ValueKind == JsonValueKind.Number
                        ? v.GetInt32()
                        : null;
                }

                static decimal? GetDecimal(JsonElement el, string propertyName)
                {
                    if (!el.TryGetProperty(propertyName, out var v))
                        return null;

                    return v.ValueKind switch
                    {
                        JsonValueKind.Number => v.GetDecimal(),
                        JsonValueKind.String when decimal.TryParse(
                            v.GetString(),
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture,
                            out var d) => d,
                        _ => null
                    };
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}