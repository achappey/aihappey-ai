using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cortex;

public partial class CortexProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var modelTasks = BackendBaseUris.Keys
                    .Select(backend => ListBackendModelsAsync(backend, ct));

                var modelSets = await Task.WhenAll(modelTasks);

                return modelSets
                    .SelectMany(a => a)
                    .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(a => a.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<List<Model>> ListBackendModelsAsync(string backend, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GetBackendUrl(backend, "v1/models"));
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Cortex API error for backend '{backend}': {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseBackendModels(backend, doc.RootElement);
    }

    private List<Model> ParseBackendModels(string backend, JsonElement root)
    {
        var models = new List<Model>();

        var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            var rawId = el.TryGetProperty("id", out var idEl)
                ? idEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(rawId))
                continue;

            var model = new Model
            {
                Id = ToBackendModelId(backend, rawId),
                Name = rawId,
                OwnedBy = el.TryGetProperty("owned_by", out var orgEl)
                    ? orgEl.GetString() ?? ""
                    : ""
            };

            models.Add(model);
        }

        return models;
    }
}
