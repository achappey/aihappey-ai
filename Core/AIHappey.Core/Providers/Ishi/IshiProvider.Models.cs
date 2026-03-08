using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Ishi;

public partial class IshiProvider
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
                    throw new Exception($"Ishi API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                        model.Id = idEl.GetString()?.Replace("pixelml/", string.Empty)?.ToModelId(GetIdentifier()) ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Id;

                    if (el.TryGetProperty("family", out var famEl))
                        model.OwnedBy = famEl.GetString() ?? "";

                    if (el.TryGetProperty("limit", out var limitEl) &&
                        limitEl.TryGetProperty("context", out var ctxEl) &&
                        ctxEl.ValueKind == JsonValueKind.Number)
                        model.ContextWindow = ctxEl.GetInt32();


                    if (el.TryGetProperty("limit", out var limitEl2) &&
                        limitEl2.TryGetProperty("output", out var maxOutEl) &&
                        maxOutEl.ValueKind == JsonValueKind.Number)
                        model.MaxTokens = maxOutEl.GetInt32();

                    if (el.TryGetProperty("cost", out var costEl) &&
                        costEl.ValueKind == JsonValueKind.Object)
                    {
                        var inputPrice = costEl.TryGetProperty("input", out var inEl)
                            ? inEl.GetRawText()
                            : null;

                        var outputPrice = costEl.TryGetProperty("output", out var outEl)
                            ? outEl.GetRawText()
                            : null;

                        if (!string.IsNullOrEmpty(inputPrice) &&
                            !string.IsNullOrEmpty(outputPrice))
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = decimal.Parse(inputPrice, CultureInfo.InvariantCulture),
                                Output = decimal.Parse(outputPrice, CultureInfo.InvariantCulture)
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