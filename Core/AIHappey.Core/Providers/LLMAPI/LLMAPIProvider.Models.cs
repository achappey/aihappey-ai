using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.LLMAPI;

public partial class LLMAPIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"LLMAPI API error: {err}");
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

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("family", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? model.Id;

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                    pricingEl.ValueKind == JsonValueKind.Object)
            {
                static decimal? ParseDecimal(JsonElement element)
                {
                    return element.ValueKind switch
                    {
                        JsonValueKind.String when decimal.TryParse(
                            element.GetString(),
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture,
                            out var d) => d,

                        JsonValueKind.Number => element.GetDecimal(),

                        _ => null
                    };
                }

                pricingEl.TryGetProperty("prompt", out var promptEl);
                pricingEl.TryGetProperty("completion", out var completionEl);
                pricingEl.TryGetProperty("input_cache_read", out var cacheReadEl);
                pricingEl.TryGetProperty("input_cache_write", out var cacheWriteEl);

                var input = ParseDecimal(promptEl);
                var output = ParseDecimal(completionEl);
                var cacheRead = ParseDecimal(cacheReadEl);
                var cacheWrite = ParseDecimal(cacheWriteEl);

                if (input.HasValue && output.HasValue &&
                    input.Value > 0 && output.Value > 0)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = input.Value,
                        Output = output.Value,
                        InputCacheRead = cacheRead > 0 ? cacheRead : null,
                        InputCacheWrite = cacheWrite > 0 ? cacheWrite : null
                    };
                }
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}