using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync<List<Model>>(
            cacheKey,
            async ct =>
            {
                var models = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);

                AddModels(models, await ListCatalogModels("v3/images/models", "image", ct));
                AddModels(models, await ListCatalogModels("v3/videos/models", "video", ct));
                AddModels(models, await ListCatalogModels("v3/audio/models", null, ct));
                AddModels(models, await ListCatalogModels("v3/models?type=llm", "language", ct));
                AddModels(models, GetIdentifier().GetModels());

                return [.. models.Values];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListCatalogModels(
        string relativeUrl,
        string? modelType,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OpperAI API error: {await resp.Content.ReadAsStringAsync(cancellationToken)}");

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseModels(doc.RootElement, modelType).ToList();
    }

    private static void AddModels(
        IDictionary<string, Model> models,
        IEnumerable<Model> modelsToAdd)
    {
        foreach (var model in modelsToAdd)
        {
            if (!string.IsNullOrEmpty(model.Id) && !models.ContainsKey(model.Id))
                models.Add(model.Id, model);
        }
    }

    private IEnumerable<Model> ParseModels(JsonElement root, string? modelType = null)
    {
        var models = new List<Model>();

        if (!root.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in arr.EnumerateArray())
        {
            Model model = new();

            if (el.TryGetProperty("id", out var idEl))
            {
                var id = idEl.GetString();
                model.Id = id?.ToModelId(GetIdentifier()) ?? "";
            }

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? "";

            if (el.TryGetProperty("provider_display_name", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                model.Type = modelType ?? MapModelType(type);
            }
            else if (!string.IsNullOrWhiteSpace(modelType))
            {
                model.Type = modelType;
            }

            // pricing
            if (el.TryGetProperty("pricing", out var pricingEl))
            {
                decimal? input = null;
                decimal? output = null;

                if (pricingEl.TryGetProperty("input", out var inEl) &&
                    inEl.ValueKind == JsonValueKind.Array &&
                    inEl.GetArrayLength() > 0 &&
                    inEl[0].ValueKind == JsonValueKind.Number)
                {
                    input = inEl[0].GetDecimal();
                }

                if (pricingEl.TryGetProperty("output", out var outEl) &&
                    outEl.ValueKind == JsonValueKind.Array &&
                    outEl.GetArrayLength() > 0 &&
                    outEl[0].ValueKind == JsonValueKind.Number)
                {
                    output = outEl[0].GetDecimal();
                }

                if (input > 0 || output > 0)
                {
                    model.Pricing = new ModelPricing
                    {
                        Input = input ?? 0,
                        Output = output ?? 0
                    };
                }
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }

    private static string MapModelType(string? type)
        => type?.ToLowerInvariant() switch
        {
            "llm" => "language",
            "tts" => "speech",
            "stt" => "transcription",
            "rerank" => "reranking",
            null or "" => "language",
            _ => type.ToLowerInvariant()
        };
}
