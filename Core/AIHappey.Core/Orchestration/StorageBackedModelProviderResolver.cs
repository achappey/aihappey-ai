using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Orchestration;

public class StorageBackedModelProviderResolver(
    IApiKeyResolver apiKeyResolver,
    IEnumerable<IModelProvider> providers,
    IHttpClientFactory httpClientFactory,
    IModelListingSnapshotStore snapshotStore,
    IModelListingRefreshQueue refreshQueue,
    AsyncCacheHelper memoryCache,
    IOptions<ModelListingStorageOptions> options,
    ILogger<StorageBackedModelProviderResolver> logger) : IAIModelProviderResolver
{
    private const string AggregateMemoryCachePrefix = "resolver:aggregate:";
    private readonly ModelListingStorageOptions _options = options.Value;
    private readonly IApiKeyPresenceResolver? _apiKeyPresenceResolver = apiKeyResolver as IApiKeyPresenceResolver;
    private readonly HashSet<string> _alwaysIncludeProviders = (options.Value.AlwaysIncludeProviders ?? [])
        .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
        .Select(providerId => providerId.Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        var refreshedProviderSnapshot = await RefreshProviderSnapshotAsync(provider, ct);
        if (refreshedProviderSnapshot == null)
            return;

        var refreshed = await BuildAggregateFromStoredSnapshotsAsync(
            provider.GetIdentifier(),
            providerCacheKey,
            refreshedProviderSnapshot,
            ct);
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
                TriggerBackgroundProviderRefresh(providerState.ProviderId, providerState.CacheKey);
        }

        return response.ModelProviderMap;
    }

    private async Task<AggregateModelsCacheEntry> LoadAggregateResponseAsync(CancellationToken ct)
    {
        var aggregateKey = BuildAggregateSnapshotKey();
        var aggregateSnapshot = await GetPreferredAggregateSnapshotAsync(aggregateKey, ct);

        var exactBaseline = TryCreateAggregateBaseline(aggregateSnapshot);
        if (exactBaseline != null)
        {
            if (UseKeyedFirstProviderSelection)
            {
                var expandedExact = await TryExpandWithCachedNonKeyedProvidersAsync(exactBaseline, ct);
                if (expandedExact != null)
                    exactBaseline = expandedExact;
            }

            var repairedExact = await TryRepairAggregateFromMissingProviderSnapshotsAsync(
                exactBaseline,
                aggregateSnapshot!.RefreshAfterUtc,
                ct);

            if (repairedExact != null)
            {
                await SaveAggregateSnapshotAsync(repairedExact, ct);

                if (aggregateSnapshot.RefreshAfterUtc <= DateTimeOffset.UtcNow)
                    TriggerBackgroundAggregateRefresh();

                return repairedExact;
            }

            logger.LogInformation(
                "Serving exact aggregate snapshot {AggregateKey} with {ModelCount} models across {ProviderCount} providers.",
                aggregateSnapshot!.AggregateKey,
                exactBaseline.ModelProviderMap.Count,
                aggregateSnapshot.Providers.Count);

            if (aggregateSnapshot.RefreshAfterUtc <= DateTimeOffset.UtcNow)
                TriggerBackgroundAggregateRefresh();

            return CreateCacheEntryFromBaseline(exactBaseline, aggregateSnapshot.RefreshAfterUtc);
        }

        if (!_options.IncludeApiKeysInSnapshotIdentity || UseKeyedFirstProviderSelection)
        {
            var latestAggregateSnapshot = await snapshotStore.GetLatestAggregateSnapshotAsync(ct);
            var latestBaseline = TryCreateAggregateBaseline(latestAggregateSnapshot);
            if (latestBaseline != null)
            {
                var repairedLatest = await TryRepairAggregateFromMissingProviderSnapshotsAsync(
                    latestBaseline,
                    latestAggregateSnapshot!.RefreshAfterUtc,
                    ct);

                if (repairedLatest != null)
                {
                    await SaveAggregateSnapshotAsync(repairedLatest, ct);
                    TriggerBackgroundAggregateRefresh();
                    return repairedLatest;
                }

                logger.LogWarning(
                    "Exact aggregate snapshot {AggregateKey} was unavailable. Serving latest aggregate snapshot {LatestAggregateKey} with {ModelCount} models across {ProviderCount} providers while a background refresh rebuilds the current key.",
                    aggregateKey,
                    latestAggregateSnapshot!.AggregateKey,
                    latestBaseline.ModelProviderMap.Count,
                    latestAggregateSnapshot.Providers.Count);

                TriggerBackgroundAggregateRefresh();

                return CreateCacheEntryFromBaseline(latestBaseline, latestAggregateSnapshot.RefreshAfterUtc);
            }
        }

        logger.LogInformation(
            "No usable aggregate snapshot was found for {AggregateKey}. Building a live aggregate.",
            aggregateKey);

        var live = await BuildAggregateResponseAsync(ct);
        await SaveAggregateSnapshotAsync(live, ct);
        return live;
    }

    private async Task<AggregateModelsCacheEntry> BuildAggregateResponseAsync(CancellationToken ct)
    {
        await _aggregateRefreshLock.WaitAsync(ct);
        try
        {
            var baseline = await GetAggregateBaselineAsync(ct);
            var baselineProviderStates = baseline?.Snapshot.Providers
                .GroupBy(state => state.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(state => state.StoredAtUtc).First(),
                    StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, StoredResolvedProviderState>(StringComparer.OrdinalIgnoreCase);

            var providerSnapshots = new Dictionary<IModelProvider, StoredProviderModelSnapshot>();
            var providerArray = GetAggregateProviders().OrderBy(_ => Random.Shared.Next()).ToArray();

            if (UseKeyedFirstProviderSelection && providerArray.Length == 0)
            {
                logger.LogInformation(
                    "Keyed-first model discovery is active for {AggregateKey}. No request-keyed providers were found; only non-expired cached providers from baseline are eligible.",
                    BuildAggregateSnapshotKey());
            }

            await Parallel.ForEachAsync(
                providerArray,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(providerArray.Length, Math.Max(1, _options.MaxParallelFirstLoad)),
                    CancellationToken = ct
                },
                async (provider, token) =>
                {
                    baselineProviderStates.TryGetValue(provider.GetIdentifier(), out var providerState);

                    var snapshot = await GetOrRefreshProviderSnapshotAsync(
                        provider,
                        providerState?.SourceCacheKey ?? providerState?.CacheKey,
                        token);

                    if (snapshot?.Models.Count > 0)
                    {
                        lock (providerSnapshots)
                        {
                            providerSnapshots[provider] = snapshot;
                        }
                    }
                });

            var mergeResult = MergeProviderSnapshots(providerSnapshots, baseline);
            var merged = mergeResult.ModelProviderMap;
            if (merged.Count == 0)
            {
                if (baseline != null)
                {
                    logger.LogWarning(
                        "Live aggregate rebuild for {AggregateKey} produced no models. Reusing baseline aggregate {BaselineAggregateKey} with {ModelCount} models across {ProviderCount} providers.",
                        BuildAggregateSnapshotKey(),
                        baseline.Snapshot.AggregateKey,
                        baseline.ModelProviderMap.Count,
                        baseline.Snapshot.Providers.Count);

                    return CreateCacheEntryFromBaseline(baseline, BuildAggregateRefreshAfterUtc());
                }

                if (!_options.DisableModelDiscovery)
                    throw new InvalidOperationException("No models resolved from any provider.");

                var emptyRefreshAfterUtc = DateTimeOffset.UtcNow
                    .Add(_options.AggregateRefreshAfter)
                    .AddMinutes(Random.Shared.Next(0, Math.Max(1, _options.AggregateRefreshJitterMinutes)));

                return new AggregateModelsCacheEntry(merged, emptyRefreshAfterUtc, []);
            }

            await EnrichModelsAsync(merged, ct);

            var refreshAfterUtc = BuildAggregateRefreshAfterUtc();
            var providerStates = BuildProviderStates(providerSnapshots, mergeResult.PreservedProviderStates);

            logger.LogInformation(
                "Built aggregate {AggregateKey} with {ModelCount} models. Refreshed providers: {RefreshedProviderCount}. Preserved providers from baseline: {PreservedProviderCount}.",
                BuildAggregateSnapshotKey(),
                merged.Count,
                providerSnapshots.Count,
                mergeResult.PreservedProviderStates.Count);

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
        StoredProviderModelSnapshot refreshedProviderSnapshot,
        CancellationToken ct)
    {
        await _aggregateRefreshLock.WaitAsync(ct);
        try
        {
            var aggregateSnapshot = await GetPreferredAggregateSnapshotAsync(BuildAggregateSnapshotKey(), ct);
            var baseline = await GetAggregateBaselineAsync(aggregateSnapshot, ct);
            var providerSnapshots = new Dictionary<IModelProvider, StoredProviderModelSnapshot>();
            var refreshedProvider = providers.FirstOrDefault(p =>
                string.Equals(p.GetIdentifier(), refreshedProviderId, StringComparison.OrdinalIgnoreCase));

            if (refreshedProvider != null)
                providerSnapshots[refreshedProvider] = refreshedProviderSnapshot;

            var mergeResult = MergeProviderSnapshots(providerSnapshots, baseline);
            var merged = mergeResult.ModelProviderMap;
            if (merged.Count == 0)
            {
                if (baseline != null)
                {
                    logger.LogWarning(
                        "Stored aggregate rebuild for {AggregateKey} after refreshing provider {ProviderId} produced no models. Reusing baseline aggregate {BaselineAggregateKey} with {ModelCount} models.",
                        BuildAggregateSnapshotKey(),
                        refreshedProviderId,
                        baseline.Snapshot.AggregateKey,
                        baseline.ModelProviderMap.Count);

                    return CreateCacheEntryFromBaseline(baseline, BuildAggregateRefreshAfterUtc());
                }

                return new AggregateModelsCacheEntry(merged, BuildAggregateRefreshAfterUtc(), []);
            }

            await EnrichModelsAsync(merged, ct);

            logger.LogInformation(
                "Rebuilt aggregate {AggregateKey} after refreshing provider {ProviderId}. Models: {ModelCount}. Updated providers: {UpdatedProviderCount}. Preserved providers from baseline: {PreservedProviderCount}.",
                BuildAggregateSnapshotKey(),
                refreshedProviderId,
                merged.Count,
                providerSnapshots.Count,
                mergeResult.PreservedProviderStates.Count);

            return new AggregateModelsCacheEntry(
                merged,
                BuildAggregateRefreshAfterUtc(),
                BuildProviderStates(providerSnapshots, mergeResult.PreservedProviderStates));
        }
        finally
        {
            _aggregateRefreshLock.Release();
        }
    }

    private async Task<StoredProviderModelSnapshot?> GetOrRefreshProviderSnapshotAsync(
        IModelProvider provider,
        string? sourceProviderCacheKey,
        CancellationToken ct)
    {
        if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
            return null;

        var snapshot = await LoadServableProviderSnapshotAsync(
            provider.GetIdentifier(),
            providerCacheKey,
            sourceProviderCacheKey ?? providerCacheKey,
            queueRefreshIfStale: true,
            ct);

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
            return await LoadServableProviderSnapshotAsync(
                provider.GetIdentifier(),
                providerCacheKey,
                providerCacheKey,
                queueRefreshIfStale: false,
                ct);
        }
    }

    private AggregateMergeResult MergeProviderSnapshots(
        Dictionary<IModelProvider, StoredProviderModelSnapshot> snapshots,
        AggregateBaseline? baseline)
    {
        var merged = new Dictionary<string, (Model Model, IModelProvider Provider)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, snapshot) in snapshots)
        {
            foreach (var model in snapshot.Models)
                merged[model.Id] = (model, provider);
        }

        if (baseline == null)
            return new AggregateMergeResult(merged, []);

        var refreshedProviderIds = snapshots.Keys
            .Select(provider => provider.GetIdentifier())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var preservedProviderStates = baseline.Snapshot.Providers
            .Where(state => state.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .GroupBy(state => state.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(state => state.StoredAtUtc).First())
            .Where(state => !refreshedProviderIds.Contains(state.ProviderId))
            .ToList();

        var preservedProviderIds = preservedProviderStates
            .Select(state => state.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in baseline.Snapshot.Entries)
        {
            if (!preservedProviderIds.Contains(entry.ProviderId) || string.IsNullOrWhiteSpace(entry.Model.Id))
                continue;

            var provider = providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
                continue;

            merged.TryAdd(entry.Model.Id, (entry.Model, provider));
        }

        return new AggregateMergeResult(merged, preservedProviderStates);
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

        logger.LogInformation(
            "Saving aggregate snapshot {AggregateKey} with {ModelCount} models across {ProviderCount} providers. RefreshAfterUtc={RefreshAfterUtc}.",
            aggregateKey,
            entry.ModelProviderMap.Count,
            entry.ProviderStates.Count,
            entry.RefreshAfterUtc);

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

    private void TriggerBackgroundProviderRefresh(string providerId, string providerCacheKey)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await QueueProviderRefreshAsync(providerId, providerCacheKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to queue provider refresh for {ProviderId} with cache key {CacheKey}.",
                    providerId,
                    providerCacheKey);
            }
        });
    }

    private void TriggerBackgroundProviderAliasBackfill(
        string providerId,
        string providerCacheKey,
        StoredProviderModelSnapshot snapshot)
    {
        if (_options.IncludeApiKeysInSnapshotIdentity)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await snapshotStore.SaveProviderSnapshotAsync(providerId, providerCacheKey, snapshot, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to backfill latest provider snapshot alias for {ProviderId} with cache key {CacheKey}.",
                    providerId,
                    providerCacheKey);
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

    private async Task<AggregateBaseline?> GetAggregateBaselineAsync(CancellationToken ct)
        => await GetAggregateBaselineAsync(
            await GetPreferredAggregateSnapshotAsync(BuildAggregateSnapshotKey(), ct),
            ct);

    private Task<StoredResolvedModelSnapshot?> GetPreferredAggregateSnapshotAsync(
        string aggregateKey,
        CancellationToken ct)
        => _options.IncludeApiKeysInSnapshotIdentity
            ? snapshotStore.GetAggregateSnapshotAsync(aggregateKey, ct)
            : snapshotStore.GetLatestAggregateSnapshotAsync(ct);

    private async Task<AggregateBaseline?> GetAggregateBaselineAsync(
        StoredResolvedModelSnapshot? preferredSnapshot,
        CancellationToken ct)
    {
        var preferredBaseline = TryCreateAggregateBaseline(preferredSnapshot);
        if (preferredBaseline != null)
            return preferredBaseline;

        if (_options.IncludeApiKeysInSnapshotIdentity)
            return null;

        var latestSnapshot = await snapshotStore.GetLatestAggregateSnapshotAsync(ct);
        return TryCreateAggregateBaseline(latestSnapshot);
    }

    private AggregateBaseline? TryCreateAggregateBaseline(StoredResolvedModelSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.Entries.Count == 0)
            return null;

        var restored = RestoreSnapshot(snapshot);
        if (restored.Count == 0)
            return null;

        return new AggregateBaseline(snapshot, restored);
    }

    private async Task<AggregateBaseline?> TryExpandWithCachedNonKeyedProvidersAsync(
        AggregateBaseline baseline,
        CancellationToken ct)
    {
        var latestSnapshot = await snapshotStore.GetLatestAggregateSnapshotAsync(ct);
        var latestBaseline = TryCreateAggregateBaseline(latestSnapshot);
        if (latestBaseline == null || latestSnapshot == null)
            return null;

        if (string.Equals(latestSnapshot.AggregateKey, baseline.Snapshot.AggregateKey, StringComparison.OrdinalIgnoreCase))
            return null;

        var now = DateTimeOffset.UtcNow;
        var cachedProviderIds = latestSnapshot.Providers
            .Where(state => state.ExpiresAtUtc > now)
            .GroupBy(state => state.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(state => state.StoredAtUtc).First())
            .Select(state => state.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (cachedProviderIds.Count == 0)
            return null;

        var merged = new Dictionary<string, (Model Model, IModelProvider Provider)>(baseline.ModelProviderMap, StringComparer.OrdinalIgnoreCase);
        var initialCount = merged.Count;

        foreach (var entry in latestSnapshot.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Model.Id) || !cachedProviderIds.Contains(entry.ProviderId))
                continue;

            var provider = providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
                continue;

            if (HasConfiguredProviderKey(provider))
                continue;

            merged.TryAdd(entry.Model.Id, (entry.Model, provider));
        }

        var additionalStates = latestSnapshot.Providers
            .Where(state => cachedProviderIds.Contains(state.ProviderId))
            .Where(state =>
            {
                var provider = providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), state.ProviderId, StringComparison.OrdinalIgnoreCase));
                return provider != null && !HasConfiguredProviderKey(provider);
            })
            .ToList();

        if (merged.Count == initialCount && additionalStates.Count == 0)
            return null;

        var mergedSnapshot = new StoredResolvedModelSnapshot
        {
            AggregateKey = baseline.Snapshot.AggregateKey,
            StoredAtUtc = baseline.Snapshot.StoredAtUtc,
            RefreshAfterUtc = baseline.Snapshot.RefreshAfterUtc,
            ExpiresAtUtc = baseline.Snapshot.ExpiresAtUtc,
            Entries = [..
                merged.Values.Select(v => new StoredResolvedModelEntry
                {
                    ProviderId = v.Provider.GetIdentifier(),
                    Model = v.Model
                })],
            Providers = [.. NormalizeProviderStates(baseline.Snapshot.Providers.Concat(additionalStates))]
        };

        logger.LogInformation(
            "Expanded keyed-first aggregate {AggregateKey} with {AddedModelCount} cached non-keyed models from latest aggregate snapshot {LatestAggregateKey}.",
            baseline.Snapshot.AggregateKey,
            merged.Count - initialCount,
            latestSnapshot.AggregateKey);

        return new AggregateBaseline(mergedSnapshot, merged);
    }

    private AggregateModelsCacheEntry CreateCacheEntryFromBaseline(
        AggregateBaseline baseline,
        DateTimeOffset refreshAfterUtc)
        => new(
            new Dictionary<string, (Model Model, IModelProvider Provider)>(baseline.ModelProviderMap, StringComparer.OrdinalIgnoreCase),
            refreshAfterUtc,
            [.. NormalizeProviderStates(baseline.Snapshot.Providers)]);

    private async Task<AggregateModelsCacheEntry?> TryRepairAggregateFromMissingProviderSnapshotsAsync(
        AggregateBaseline baseline,
        DateTimeOffset refreshAfterUtc,
        CancellationToken ct)
    {
        var knownProviderIds = baseline.Snapshot.Providers
            .Select(state => state.ProviderId)
            .Concat(baseline.Snapshot.Entries.Select(entry => entry.ProviderId))
            .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingProviders = GetAggregateProviders()
            .Where(provider => !knownProviderIds.Contains(provider.GetIdentifier()))
            .GroupBy(provider => provider.GetIdentifier(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (missingProviders.Length == 0)
            return null;

        var recoveredSnapshots = new Dictionary<IModelProvider, StoredProviderModelSnapshot>();

        foreach (var provider in missingProviders)
        {
            if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
                continue;

            var snapshot = await LoadServableProviderSnapshotAsync(
                provider.GetIdentifier(),
                providerCacheKey,
                providerCacheKey,
                queueRefreshIfStale: true,
                ct)
                ?? await RefreshProviderSnapshotAsync(provider, ct);

            if (snapshot == null)
            {
                logger.LogWarning(
                    "Aggregate snapshot {AggregateKey} is missing configured provider {ProviderId}, but no servable or freshly refreshed provider snapshot was available.",
                    baseline.Snapshot.AggregateKey,
                    provider.GetIdentifier());

                continue;
            }

            recoveredSnapshots[provider] = snapshot;
        }

        if (recoveredSnapshots.Count == 0)
            return null;

        var merged = new Dictionary<string, (Model Model, IModelProvider Provider)>(baseline.ModelProviderMap, StringComparer.OrdinalIgnoreCase);
        var originalModelCount = merged.Count;

        foreach (var (provider, snapshot) in recoveredSnapshots)
        {
            foreach (var model in snapshot.Models)
                merged[model.Id] = (model, provider);
        }

        await EnrichModelsAsync(merged, ct);

        var reconciled = new AggregateModelsCacheEntry(
            merged,
            refreshAfterUtc,
            BuildProviderStates(recoveredSnapshots, [.. NormalizeProviderStates(baseline.Snapshot.Providers)]));

        logger.LogWarning(
            "Aggregate snapshot {AggregateKey} was missing configured providers {ProviderIds}. Recovered {AddedModelCount} additional models from provider snapshots and repaired the aggregate.",
            baseline.Snapshot.AggregateKey,
            string.Join(", ", recoveredSnapshots.Keys.Select(provider => provider.GetIdentifier()).OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)),
            merged.Count - originalModelCount);

        return reconciled;
    }

    private async Task<StoredProviderModelSnapshot?> LoadServableProviderSnapshotAsync(
        string providerId,
        string providerCacheKey,
        string? sourceProviderCacheKey,
        bool queueRefreshIfStale,
        CancellationToken ct)
    {
        var snapshot = _options.IncludeApiKeysInSnapshotIdentity
            ? await snapshotStore.GetProviderSnapshotAsync(providerId, providerCacheKey, ct)
            : await snapshotStore.GetLatestProviderSnapshotAsync(providerId, ct);

        if (!_options.IncludeApiKeysInSnapshotIdentity
            && snapshot == null
            && !string.IsNullOrWhiteSpace(sourceProviderCacheKey))
        {
            snapshot = await snapshotStore.GetProviderSnapshotAsync(providerId, sourceProviderCacheKey, ct);

            if (snapshot != null)
            {
                TriggerBackgroundProviderAliasBackfill(providerId, providerCacheKey, snapshot);
            }
        }

        if (snapshot == null || snapshot.Models.Count == 0)
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

    private List<StoredResolvedProviderState> BuildProviderStates(
        Dictionary<IModelProvider, StoredProviderModelSnapshot> providerSnapshots,
        IReadOnlyCollection<StoredResolvedProviderState>? preservedProviderStates = null)
    {
        var states = providerSnapshots
            .Select(kvp => new StoredResolvedProviderState
            {
                ProviderId = kvp.Key.GetIdentifier(),
                CacheKey = kvp.Value.CacheKey,
                SourceCacheKey = kvp.Value.CacheKey,
                StoredAtUtc = kvp.Value.StoredAtUtc,
                RefreshAfterUtc = kvp.Value.RefreshAfterUtc,
                ExpiresAtUtc = kvp.Value.ExpiresAtUtc
            })
            .ToList();

        if (preservedProviderStates != null && preservedProviderStates.Count > 0)
            states.AddRange(preservedProviderStates);

        return [.. NormalizeProviderStates(states)];
    }

    private string BuildAggregateSnapshotKey()
    {
        var parts = GetAggregateProviders()
            .Select(provider =>
            {
                if (!_options.IncludeApiKeysInSnapshotIdentity)
                    return provider.GetIdentifier();

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
        => UseKeyedFirstProviderSelection
            ? GetConfiguredProviders().Where(HasConfiguredProviderKey)
            : !_options.DisableModelDiscovery
            ? GetConfiguredProviders()
            : GetConfiguredProviders().Where(HasConfiguredApiKey);

    private bool UseKeyedFirstProviderSelection
        => _options.IncludeApiKeysInSnapshotIdentity && _apiKeyPresenceResolver != null;

    private bool HasConfiguredProviderKey(IModelProvider provider)
        => _apiKeyPresenceResolver?.HasConfiguredKey(provider.GetIdentifier()) == true
           || _alwaysIncludeProviders.Contains(provider.GetIdentifier());

    private bool HasConfiguredApiKey(IModelProvider provider)
        => !string.IsNullOrWhiteSpace(apiKeyResolver.Resolve(provider.GetIdentifier()));

    private bool TryGetProviderCacheKey(IModelProvider provider, out string cacheKey)
    {
        var apiKey = _options.IncludeApiKeysInSnapshotIdentity
            ? apiKeyResolver.Resolve(provider.GetIdentifier())
            : null;

        cacheKey = provider.GetCacheKey(apiKey);
        return true;
    }

    private IEnumerable<StoredResolvedProviderState> NormalizeProviderStates(IEnumerable<StoredResolvedProviderState> states)
    {
        foreach (var state in states
                     .GroupBy(state => state.ProviderId, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(state => state.StoredAtUtc).First()))
        {
            if (_options.IncludeApiKeysInSnapshotIdentity)
            {
                yield return state;
                continue;
            }

            var provider = providers.FirstOrDefault(p => string.Equals(p.GetIdentifier(), state.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider == null || !TryGetProviderCacheKey(provider, out var normalizedCacheKey))
            {
                yield return state;
                continue;
            }

            yield return new StoredResolvedProviderState
            {
                ProviderId = state.ProviderId,
                CacheKey = normalizedCacheKey,
                SourceCacheKey = state.SourceCacheKey ?? state.CacheKey,
                StoredAtUtc = state.StoredAtUtc,
                RefreshAfterUtc = state.RefreshAfterUtc,
                ExpiresAtUtc = state.ExpiresAtUtc
            };
        }
    }

    private static string BuildQueuedProviderKey(string providerId, string providerCacheKey) => $"{providerId}:{providerCacheKey}";

    private sealed record AggregateModelsCacheEntry(
        Dictionary<string, (Model Model, IModelProvider Provider)> ModelProviderMap,
        DateTimeOffset RefreshAfterUtc,
        IReadOnlyList<StoredResolvedProviderState> ProviderStates);

    private sealed record AggregateBaseline(
        StoredResolvedModelSnapshot Snapshot,
        Dictionary<string, (Model Model, IModelProvider Provider)> ModelProviderMap);

    private sealed record AggregateMergeResult(
        Dictionary<string, (Model Model, IModelProvider Provider)> ModelProviderMap,
        IReadOnlyList<StoredResolvedProviderState> PreservedProviderStates);
}
