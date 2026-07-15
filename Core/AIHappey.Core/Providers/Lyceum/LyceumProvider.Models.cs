using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Lyceum;

public partial class LyceumProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var models = new List<Model>();
                await AddLyceumChatModelsAsync(models, cancellationToken);
                await AddLyceumImageModelsAsync(models, cancellationToken);

                return models.DistinctBy(model => model.Id).ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task AddLyceumChatModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "serverless/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Lyceum API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

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
                model.Type = model.Name.GuessModelType();
            }

            model.Created = el.TryGetProperty("created", out var c) &&
                c.ValueKind == JsonValueKind.Number
                    ? c.GetInt64()
                    : null;

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }
    }

    private async Task AddLyceumImageModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "images/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Lyceum image models API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;

            models.Add(new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                Type = "image",
                OwnedBy = nameof(Lyceum)
            });
        }
    }
}
