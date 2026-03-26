using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.NebulaBlock;

public partial class NebulaBlockProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/serverless/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"NebulaBlock API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr =
                    root.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("models", out var modelsEl) &&
                    modelsEl.ValueKind == JsonValueKind.Array
                        ? modelsEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("model_name", out var idEl))
                    {
                        var id = idEl.GetString();
                        model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                    }

                    if (el.TryGetProperty("model_alias", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Id;
                    else
                        model.Name = model.Id;

                    if (el.TryGetProperty("context_length", out var ctxEl) &&
                        ctxEl.ValueKind == JsonValueKind.Number)
                    {
                        model.ContextWindow = ctxEl.GetInt32();
                    }

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("description", out var descriptionEl))
                        model.Description = descriptionEl.GetString() ?? "";

                    decimal? input = null;
                    decimal? output = null;

                    if (el.TryGetProperty("input_price", out var inEl))
                    {
                        if (inEl.ValueKind == JsonValueKind.Number)
                            input = inEl.GetDecimal();
                        else if (inEl.ValueKind == JsonValueKind.String &&
                                 decimal.TryParse(inEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                            input = d;
                    }

                    if (el.TryGetProperty("output_price", out var outEl))
                    {
                        if (outEl.ValueKind == JsonValueKind.Number)
                            output = outEl.GetDecimal();
                        else if (outEl.ValueKind == JsonValueKind.String &&
                                 decimal.TryParse(outEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                            output = d;
                    }

                    if (input.HasValue && output.HasValue && input != 0 && output != 0)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = input.Value,
                            Output = output.Value
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