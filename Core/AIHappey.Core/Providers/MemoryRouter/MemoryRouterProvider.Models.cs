using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.MemoryRouter;

public partial class MemoryRouterProvider
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

                using var resp = await _client.GetAsync("v1/models", cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"MemoryRouter API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var root = doc.RootElement;

                var ids =
                    Enumerable.Concat(
                        root.TryGetProperty("default", out var defEl)
                            ? new[] { defEl.GetString() }
                            : Array.Empty<string>(),
                        root.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array
                            ? modelsEl.EnumerateArray().Select(x => x.GetString())
                            : Enumerable.Empty<string>()
                    )
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                return ids.Select(id => new Model
                {
                    Id = id!.ToModelId(GetIdentifier()),
                    Name = id!
                });
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}