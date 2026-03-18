using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Storage;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Orchestration;

public class StorageBackedModelProviderResolver(
    IApiKeyResolver apiKeyResolver,
    IEnumerable<IModelProvider> providers,
    IHttpClientFactory httpClientFactory,
    IModelListingSnapshotStore snapshotStore,
    IModelListingRefreshQueue refreshQueue,
    AsyncCacheHelper memoryCache,
    IOptions<ModelListingStorageOptions> options) : IAIModelProviderResolver
{
    private const string AggregateMemoryCachePrefix = "resolver:aggregate:";
    private readonly ModelListingStorageOptions _options = options.Value;
    private readonly SemaphoreSlim _aggregateRefreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _queuedRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private int _backgroundAggregateRefreshRunning;

    public async Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
    {
        var map = await GetAggregateMapAsync(ct);

        if (map.TryGetValue(model, out var entry))
            return entry.Provider;

        var key = map.Keys.FirstOrDefault(z => z.SplitModelId().Model == model);
        if (key != null && map.TryGetValue(key, out var fallbackEntry))
            return fallbackEntry.Provider;

        throw new NotSupportedException($"No provider found for model '{model}'.");
    }

    public IModelProvider GetProvider() => providers
        .FirstOrDefault(p => !string.IsNullOrEmpty(apiKeyResolver.Resolve(p.GetIdentifier())))
        ?? providers.FirstOrDefault(a => a.GetIdentifier() == "pollinations")
        ?? throw new NotSupportedException("No providers found");

    public async Task<ModelResponse> ResolveModels(CancellationToken ct)
    {
        var map = await GetAggregateMapAsync(ct);

        return new ModelResponse
        {
            Data = [..
                map.Values
                    .Select(v => v.Model)
                    .Where(a => a.Type != "embedding")
                    .OrderByDescending(m => m.Created)]
        };
    }

    public async Task RefreshQueuedProviderAsync(ModelListingRefreshRequest request, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), request.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
            return;

        if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
            return;

        if (!string.Equals(providerCacheKey, request.CacheKey, StringComparison.Ordinal))
            return;

        await RefreshProviderSnapshotAsync(provider, ct);

        var refreshed = await BuildAggregateFromStoredSnapshotsAsync(provider.GetIdentifier(), providerCacheKey, ct);
        if (refreshed.ModelProviderMap.Count == 0)
            return;

        await SaveAggregateSnapshotAsync(refreshed, ct);
        memoryCache.Set(GetAggregateMemoryCacheKey(), refreshed, _options.MemoryCacheTtl);
    }

    private async Task<Dictionary<string, (Model Model, IModelProvider Provider)>> GetAggregateMapAsync(CancellationToken ct)
    {
        var aggregateCacheKey = GetAggregateMemoryCacheKey();

        var response = await memoryCache.GetOrCreateAsync(
            aggregateCacheKey,
            async token => await LoadAggregateResponseAsync(token),
            baseTtl: _options.MemoryCacheTtl,
            cancellationToken: ct);

        if (response.RefreshAfterUtc <= DateTimeOffset.UtcNow)
            TriggerBackgroundAggregateRefresh();

        foreach (var providerState in response.ProviderStates)
        {
            if (providerState.RefreshAfterUtc <= DateTimeOffset.UtcNow)
                await QueueProviderRefreshAsync(providerState.ProviderId, providerState.CacheKey);
        }

        return response.ModelProviderMap;
    }

    private async Task<AggregateModelsCacheEntry> LoadAggregateResponseAsync(CancellationToken ct)
    {
        var aggregateKey = BuildAggregateSnapshotKey();
        var aggregateSnapshot = await snapshotStore.GetAggregateSnapshotAsync(aggregateKey, ct);

        if (aggregateSnapshot?.Entries.Count > 0)
        {
            var restored = RestoreSnapshot(aggregateSnapshot);
            if (restored.Count > 0)
            {
                if (aggregateSnapshot.RefreshAfterUtc <= DateTimeOffset.UtcNow)
                    TriggerBackgroundAggregateRefresh();

                return new AggregateModelsCacheEntry(
                    restored,
                    aggregateSnapshot.RefreshAfterUtc,
                    [.. aggregateSnapshot.Providers]);
            }
        }

        var live = await BuildAggregateResponseAsync(ct);
        await SaveAggregateSnapshotAsync(live, ct);
        return live;
    }

    private async Task<AggregateModelsCacheEntry> BuildAggregateResponseAsync(CancellationToken ct)
    {
        await _aggregateRefreshLock.WaitAsync(ct);
        try
        {
            var providerSnapshots = new Dictionary<IModelProvider, StoredProviderModelSnapshot>();
            var providerArray = GetAggregateProviders().OrderBy(_ => Random.Shared.Next()).ToArray();

            await Parallel.ForEachAsync(
                providerArray,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(providerArray.Length, Math.Max(1, _options.MaxParallelFirstLoad)),
                    CancellationToken = ct
                },
                async (provider, token) =>
                {
                    var snapshot = await GetOrRefreshProviderSnapshotAsync(provider, token);
                    if (snapshot?.Models.Count > 0)
                    {
                        lock (providerSnapshots)
                        {
                            providerSnapshots[provider] = snapshot;
                        }
                    }
                });

            var merged = MergeProviderSnapshots(providerSnapshots);
            if (merged.Count == 0)
            {
                if (!_options.DisableModelDiscovery)
                    throw new InvalidOperationException("No models resolved from any provider.");

                var emptyRefreshAfterUtc = DateTimeOffset.UtcNow
                    .Add(_options.AggregateRefreshAfter)
                    .AddMinutes(Random.Shared.Next(0, Math.Max(1, _options.AggregateRefreshJitterMinutes)));

                return new AggregateModelsCacheEntry(merged, emptyRefreshAfterUtc, []);
            }

            await EnrichModelsAsync(merged, ct);

            var refreshAfterUtc = BuildAggregateRefreshAfterUtc();
            var providerStates = BuildProviderStates(providerSnapshots);

            return new AggregateModelsCacheEntry(merged, refreshAfterUtc, providerStates);
        }
        finally
        {
            _aggregateRefreshLock.Release();
        }
    }

    private async Task<AggregateModelsCacheEntry> BuildAggregateFromStoredSnapshotsAsync(
        string refreshedProviderId,
        string refreshedProviderCacheKey,
        CancellationToken ct)
    {
        await _aggregateRefreshLock.WaitAsync(ct);
        try
        {
            var aggregateSnapshot = await snapshotStore.GetAggregateSnapshotAsync(BuildAggregateSnapshotKey(), ct);
            var providerStates = (aggregateSnapshot?.Providers ?? [])
                .GroupBy(state => BuildQueuedProviderKey(state.ProviderId, state.CacheKey), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (!providerStates.Any(state =>
                    string.Equals(state.ProviderId, refreshedProviderId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(state.CacheKey, refreshedProviderCacheKey, StringComparison.Ordinal)))
            {
                providerStates.Add(new StoredResolvedProviderState
                {
                    ProviderId = refreshedProviderId,
                    CacheKey = refreshedProviderCacheKey
                });
            }

            var providerSnapshots = new Dictionary<IModelProvider, StoredProviderModelSnapshot>();

            foreach (var state in providerStates)
            {
                var provider = providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), state.ProviderId, StringComparison.OrdinalIgnoreCase));
                if (provider == null)
                    continue;

                var snapshot = await LoadServableProviderSnapshotAsync(state.ProviderId, state.CacheKey, queueRefreshIfStale: true, ct);
                if (snapshot == null)
                    continue;

                providerSnapshots[provider] = snapshot;
            }

            var merged = MergeProviderSnapshots(providerSnapshots);
            if (merged.Count == 0)
                return new AggregateModelsCacheEntry(merged, BuildAggregateRefreshAfterUtc(), []);

            await EnrichModelsAsync(merged, ct);
            return new AggregateModelsCacheEntry(merged, BuildAggregateRefreshAfterUtc(), BuildProviderStates(providerSnapshots));
        }
        finally
        {
            _aggregateRefreshLock.Release();
        }
    }

    private async Task<StoredProviderModelSnapshot?> GetOrRefreshProviderSnapshotAsync(IModelProvider provider, CancellationToken ct)
    {
        if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
            return null;

        var snapshot = await LoadServableProviderSnapshotAsync(provider.GetIdentifier(), providerCacheKey, queueRefreshIfStale: true, ct);
        if (snapshot != null)
            return snapshot;

        return await RefreshProviderSnapshotAsync(provider, ct);
    }

    private async Task<StoredProviderModelSnapshot?> RefreshProviderSnapshotAsync(IModelProvider provider, CancellationToken ct)
    {
        if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
            return null;

        try
        {
            var models = (await provider.ListModels(ct)).ToList();
            if (models.Count == 0)
                return null;

            var now = DateTimeOffset.UtcNow;
            var snapshot = new StoredProviderModelSnapshot
            {
                ProviderId = provider.GetIdentifier(),
                CacheKey = providerCacheKey,
                StoredAtUtc = now,
                RefreshAfterUtc = now.Add(_options.ProviderRefreshAfter).AddMinutes(Random.Shared.Next(0, Math.Max(1, _options.ProviderRefreshJitterMinutes))),
                ExpiresAtUtc = now.Add(_options.ProviderSnapshotTtl),
                Models = models
            };

            await snapshotStore.SaveProviderSnapshotAsync(provider.GetIdentifier(), providerCacheKey, snapshot, ct);
            _queuedRefreshes.TryRemove(BuildQueuedProviderKey(provider.GetIdentifier(), providerCacheKey), out _);
            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await snapshotStore.GetProviderSnapshotAsync(provider.GetIdentifier(), providerCacheKey, ct);
        }
    }

    private Dictionary<string, (Model Model, IModelProvider Provider)> MergeProviderSnapshots(Dictionary<IModelProvider, StoredProviderModelSnapshot> snapshots)
    {
        var merged = new Dictionary<string, (Model Model, IModelProvider Provider)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, snapshot) in snapshots)
        {
            foreach (var model in snapshot.Models)
                merged[model.Id] = (model, provider);
        }

        return merged;
    }

    private async Task EnrichModelsAsync(Dictionary<string, (Model Model, IModelProvider Provider)> merged, CancellationToken ct)
    {
        foreach (var key in merged.Keys.ToList())
        {
            var model = merged[key].Model;

            model.Type ??= model.Id.GuessModelType() ?? string.Empty;

        }

        /* var vercelModels = await FetchVercelModels(ct);

         foreach (var key in merged.Keys.ToList())
         {
             var enrich = vercelModels?.FirstOrDefault(v => key.EndsWith(v.Id, StringComparison.OrdinalIgnoreCase));
             var model = merged[key].Model;

             model.Type ??= model.Id.GuessModelType() ?? string.Empty;

             if (enrich == null)
                 continue;

             model.ContextWindow ??= enrich.ContextWindow;
             model.MaxTokens ??= enrich.MaxTokens;
             model.Created ??= enrich.Created;
             model.Pricing ??= enrich.Pricing;
             model.Tags ??= enrich.Tags;
             model.Type ??= enrich.Type;
             model.Description ??= enrich.Description;
             model.OwnedBy ??= enrich.OwnedBy;
         }*/

        var modelsByBase = merged.Values
            .Select(v => v.Model)
            .GroupBy(m => m.Id.Split("/").Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in modelsByBase)
        {
            var models = group.ToList();

            var contextWindow = models.FirstOrDefault(m => m.ContextWindow != null)?.ContextWindow;
            var maxTokens = models.FirstOrDefault(m => m.MaxTokens != null)?.MaxTokens;
            var created = models.FirstOrDefault(m => m.Created != null)?.Created;
            var tags = models.FirstOrDefault(m => m.Tags?.Any() == true)?.Tags;
            var description = models.FirstOrDefault(m => !string.IsNullOrEmpty(m.Description))?.Description;

            foreach (var model in models)
            {
                model.ContextWindow ??= contextWindow;
                model.MaxTokens ??= maxTokens;
                model.Created ??= created;
                model.Tags ??= tags;
                model.Description ??= description;
            }
        }
    }

    private async Task SaveAggregateSnapshotAsync(AggregateModelsCacheEntry entry, CancellationToken ct)
    {
        var aggregateKey = BuildAggregateSnapshotKey();
        var now = DateTimeOffset.UtcNow;

        await snapshotStore.SaveAggregateSnapshotAsync(
            aggregateKey,
            new StoredResolvedModelSnapshot
            {
                AggregateKey = aggregateKey,
                StoredAtUtc = now,
                RefreshAfterUtc = entry.RefreshAfterUtc,
                ExpiresAtUtc = now.Add(_options.AggregateSnapshotTtl),
                Entries = [..
                    entry.ModelProviderMap.Values.Select(v => new StoredResolvedModelEntry
                    {
                        ProviderId = v.Provider.GetIdentifier(),
                        Model = v.Model
                    })],
                Providers = [.. entry.ProviderStates]
            },
            ct);
    }

    private async Task<IEnumerable<Model>?> FetchVercelModels(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();

        try
        {
            var json = await http.GetFromJsonAsync<ModelResponse>("https://ai-gateway.vercel.sh/v1/models", ct);
            return json?.Data;
        }
        catch
        {
            return null;
        }
    }

    private void TriggerBackgroundAggregateRefresh()
    {
        if (Interlocked.Exchange(ref _backgroundAggregateRefreshRunning, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var entry = await BuildAggregateResponseAsync(CancellationToken.None);
                await SaveAggregateSnapshotAsync(entry, CancellationToken.None);
                memoryCache.Set(GetAggregateMemoryCacheKey(), entry, _options.MemoryCacheTtl);
            }
            catch
            {
                // background refresh is best effort
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundAggregateRefreshRunning, 0);
            }
        });
    }

    private async Task QueueProviderRefreshAsync(string providerId, string providerCacheKey)
    {
        var dedupeKey = BuildQueuedProviderKey(providerId, providerCacheKey);
        if (!_queuedRefreshes.TryAdd(dedupeKey, 0))
            return;

        try
        {
            await refreshQueue.EnqueueAsync(new ModelListingRefreshRequest
            {
                ProviderId = providerId,
                CacheKey = providerCacheKey
            });
        }
        catch
        {
            _queuedRefreshes.TryRemove(dedupeKey, out _);
            TriggerBackgroundAggregateRefresh();
        }
    }

    private Dictionary<string, (Model Model, IModelProvider Provider)> RestoreSnapshot(StoredResolvedModelSnapshot snapshot)
    {
        var map = new Dictionary<string, (Model Model, IModelProvider Provider)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot.Entries)
        {
            var provider = providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider == null || string.IsNullOrWhiteSpace(entry.Model.Id))
                continue;

            map[entry.Model.Id] = (entry.Model, provider);
        }

        return map;
    }

    private async Task<StoredProviderModelSnapshot?> LoadServableProviderSnapshotAsync(
        string providerId,
        string providerCacheKey,
        bool queueRefreshIfStale,
        CancellationToken ct)
    {
        var snapshot = await snapshotStore.GetProviderSnapshotAsync(providerId, providerCacheKey, ct);
        if (snapshot?.Models.Count <= 0)
            return null;

        var now = DateTimeOffset.UtcNow;

        if (snapshot?.RefreshAfterUtc <= now && queueRefreshIfStale)
            await QueueProviderRefreshAsync(providerId, providerCacheKey);

        if (snapshot?.ExpiresAtUtc <= now)
            return null;

        return snapshot;
    }

    private DateTimeOffset BuildAggregateRefreshAfterUtc()
        => DateTimeOffset.UtcNow
            .Add(_options.AggregateRefreshAfter)
            .AddMinutes(Random.Shared.Next(0, Math.Max(1, _options.AggregateRefreshJitterMinutes)));

    private static List<StoredResolvedProviderState> BuildProviderStates(Dictionary<IModelProvider, StoredProviderModelSnapshot> providerSnapshots)
        => providerSnapshots
            .Select(kvp => new StoredResolvedProviderState
            {
                ProviderId = kvp.Key.GetIdentifier(),
                CacheKey = kvp.Value.CacheKey,
                StoredAtUtc = kvp.Value.StoredAtUtc,
                RefreshAfterUtc = kvp.Value.RefreshAfterUtc,
                ExpiresAtUtc = kvp.Value.ExpiresAtUtc
            })
            .ToList();

    private string BuildAggregateSnapshotKey()
    {
        var parts = GetAggregateProviders()
            .Select(provider =>
            {
                var key = apiKeyResolver.Resolve(provider.GetIdentifier());
                var keyHash = string.IsNullOrWhiteSpace(key)
                    ? "nokey"
                    : ModelProviderExtensions.CacheKeyFromApiKey(key);

                return $"{provider.GetIdentifier()}:{keyHash}";
            })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        var raw = string.Join("|", parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"resolver:{Convert.ToHexString(hash)}";
    }

    private string GetAggregateMemoryCacheKey() => AggregateMemoryCachePrefix + BuildAggregateSnapshotKey();

    private IEnumerable<IModelProvider> GetConfiguredProviders() => providers as IModelProvider[] ?? [.. providers];

    private IEnumerable<IModelProvider> GetAggregateProviders()
        => !_options.DisableModelDiscovery
            ? GetConfiguredProviders()
            : GetConfiguredProviders().Where(HasConfiguredApiKey);

    private bool HasConfiguredApiKey(IModelProvider provider)
        => !string.IsNullOrWhiteSpace(apiKeyResolver.Resolve(provider.GetIdentifier()));

    private bool TryGetProviderCacheKey(IModelProvider provider, out string cacheKey)
    {
        var apiKey = apiKeyResolver.Resolve(provider.GetIdentifier());

        cacheKey = provider.GetCacheKey(apiKey);
        return true;
    }

    private static string BuildQueuedProviderKey(string providerId, string providerCacheKey) => $"{providerId}:{providerCacheKey}";

    private sealed record AggregateModelsCacheEntry(
        Dictionary<string, (Model Model, IModelProvider Provider)> ModelProviderMap,
        DateTimeOffset RefreshAfterUtc,
        IReadOnlyList<StoredResolvedProviderState> ProviderStates);
}
