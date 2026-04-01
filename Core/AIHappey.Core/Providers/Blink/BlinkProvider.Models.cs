using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Blink;

public partial class BlinkProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/ai/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Blink API error: {err}");
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

                    model.ContextWindow = el.TryGetProperty("context_window", out var v) &&
                        v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;

                    model.MaxTokens = el.TryGetProperty("max_tokens", out var m) &&
                            m.ValueKind == JsonValueKind.Number
                                ? m.GetInt32()
                                : null;

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? "";

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("pricing", out var pricingEl) &&
                            pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        string? inputPrice = null;
                        if (pricingEl.TryGetProperty("input", out var inEl))
                            inputPrice = inEl.ValueKind == JsonValueKind.String ? inEl.GetString() : inEl.GetRawText();

                        string? outputPrice = null;
                        if (pricingEl.TryGetProperty("output", out var outEl))
                            outputPrice = outEl.ValueKind == JsonValueKind.String ? outEl.GetString() : outEl.GetRawText();

                        if (!string.IsNullOrWhiteSpace(inputPrice) &&
                            !string.IsNullOrWhiteSpace(outputPrice) &&
                            inputPrice != "0" && outputPrice != "0" &&
                            decimal.TryParse(inputPrice, NumberStyles.Float, CultureInfo.InvariantCulture, out var input) &&
                            decimal.TryParse(outputPrice, NumberStyles.Float, CultureInfo.InvariantCulture, out var output) &&
                            input != 0m && output != 0m)
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

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}