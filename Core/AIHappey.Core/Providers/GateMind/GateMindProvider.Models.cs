using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.GateMind;

public partial class GateMindProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>();

                await AddModels(models, cancellationToken);
                await AddRouters(models, cancellationToken);

                return models
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .DistinctBy(m => m.Id);
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task AddModels(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"GateMind API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var model = new Model
            {
                Type = "language"
            };

            if (el.TryGetProperty("model_name", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : null;

            if (!string.IsNullOrWhiteSpace(model.Id))
                models.Add(model);
        }
    }

    private async Task AddRouters(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/dev/library/routers");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"GateMind router API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("nft_address", out var idEl))
                continue;

            var nftAddress = idEl.GetString();

            if (string.IsNullOrWhiteSpace(nftAddress))
                continue;

            var name = el.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : null;

            models.Add(new Model
            {
                Id = nftAddress.ToModelId(GetIdentifier()),
                Name = name ?? nftAddress,
                Type = "language",
                Description = el.TryGetProperty("models_used", out var modelsUsedEl)
                    ? $"Router using: {modelsUsedEl.GetString()}"
                    : "GateMind router"
            });
        }
    }
}