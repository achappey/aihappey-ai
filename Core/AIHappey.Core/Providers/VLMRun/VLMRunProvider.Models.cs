using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.VLMRun;

public partial class VLMRunProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

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
                    throw new Exception($"VLMRun API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("model", out var modelEl) ||
                        !el.TryGetProperty("domain", out var domainEl))
                        continue;

                    var modelName = modelEl.GetString();
                    var domain = domainEl.GetString();

                    if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(domain))
                        continue;

                    var id = $"{modelName}/{domain}";

                    models.Add(new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = id,
                        OwnedBy = modelName
                    });
                }

                models.AddRange(GetIdentifier().GetModels());

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}