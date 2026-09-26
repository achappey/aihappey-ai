using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
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

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Cortecs API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                // ✅ root is already an array
                var arr = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
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

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                var knownIds = models.Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var catalogModel in GetIdentifier().GetModels())
                {
                    if (knownIds.Add(catalogModel.Id))
                        models.Add(catalogModel);
                    else if (IsOcrModel(catalogModel.Id))
                    {
                        var liveModel = models.First(model => string.Equals(model.Id, catalogModel.Id, StringComparison.OrdinalIgnoreCase));
                        liveModel.Type = catalogModel.Type;
                        liveModel.Name = catalogModel.Name;
                        liveModel.Description = catalogModel.Description;
                    }
                }
                
                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
