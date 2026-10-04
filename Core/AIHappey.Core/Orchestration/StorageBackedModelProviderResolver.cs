using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Orchestration;

public class StorageBackedModelProviderResolver(
    IApiKeyResolver apiKeyResolver,
    ProviderRegistry providers,
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ModelListingRefreshState refreshState,
    IHttpClientFactory httpClientFactory,
    IModelListingSnapshotStore snapshotStore,
    IModelListingRefreshQueue refreshQueue,
    AsyncCacheHelper memoryCache,
    IOptions<ModelListingStorageOptions> options,
    IOptions<ModelResolverOptions> resolverOptions,
    ILogger<StorageBackedModelProviderResolver> logger) : IAIModelProviderResolver
{
    private const string AggregateMemoryCachePrefix = "resolver:aggregate:";
    private readonly ModelListingStorageOptions _options = options.Value;
    private readonly ModelResolverOptions _resolverOptions = resolverOptions.Value;
    private readonly IApiKeyPresenceResolver? _apiKeyPresenceResolver = apiKeyResolver as IApiKeyPresenceResolver;
    private readonly HashSet<string> _alwaysIncludeProviders = (options.Value.AlwaysIncludeProviders ?? [])
        .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
        .Select(providerId => providerId.Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _aggregateRefreshLock = new(1, 1);

    public async Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
    {
        // A qualified execution must not rebuild the entire catalog on a cold cache.
        // Discover/validate only its provider; aggregate discovery is for listings and
        // ambiguous, unqualified model names.
        if (model.Contains('/', StringComparison.Ordinal)
            && providers.HasModelProvider(model.SplitModelId().Provider))
        {
            var providerId = GetSelectedProviders().FirstOrDefault(id =>
                string.Equals(id, model.SplitModelId().Provider, StringComparison.OrdinalIgnoreCase));
            if (providerId == null)
                throw new ModelProviderNotFoundException(model);

            IEnumerable<Model> models;
            if (IsRequestScopedProvider(providerId))
            {
                models = await ResolveProvider(providerId).ListModels(ct);
            }
            else if (memoryCache.TryGetValue<AggregateModelsCacheEntry>(GetAggregateMemoryCacheKey(), out var cached)
                && cached!.ModelProviderMap.Values.Any(entry =>
                    string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)))
            {
                models = cached.ModelProviderMap.Values
                    .Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Model);
            }
            else
            {
                TryGetProviderCacheKey(providerId, out var cacheKey);
                var snapshot = await LoadServableProviderSnapshotAsync(providerId, cacheKey, cacheKey,
                    queueRefreshIfStale: true, ct);
                // Aggregates can exist without a separate provider snapshot.
                var baseline = snapshot == null ? await GetAggregateBaselineAsync(ct) : null;
                var baselineModels = baseline?.ModelProviderMap.Values
                    .Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Model).ToArray();
                models = snapshot != null ? snapshot.Models
                    : baselineModels is { Length: > 0 } ? baselineModels
                    : (await RefreshProviderSnapshotAsync(providerId, ct))?.Models ?? [];
            }

            var resolved = models.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, model, StringComparison.OrdinalIgnoreCase));
            if (resolved == null)
                throw new ModelProviderNotFoundException(model);
            ThrowIfDisabled(model, resolved.Id, resolved.Name);
            return ResolveProvider(providerId);
        }

        var map = await GetAggregateMapAsync(ct);

        var entry = map.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.Model.Id, model, StringComparison.OrdinalIgnoreCase));
        if (entry.Model is not null)
        {
            ThrowIfDisabled(model, entry.Model.Id, entry.Model.Name);
            return ResolveProvider(entry.ProviderId);
        }

        var fallbackEntry = map.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.Model.Id.SplitModelId().Model, model, StringComparison.OrdinalIgnoreCase));
        if (fallbackEntry.Model is not null)
        {
            ThrowIfDisabled(model, fallbackEntry.Model.Id, fallbackEntry.Model.Name);
            return ResolveProvider(fallbackEntry.ProviderId);
        }

        throw new ModelProviderNotFoundException(model);
    }

    private void ThrowIfDisabled(string requestedModel, params string?[] resolvedModelAliases)
    {
        if (!_resolverOptions.IsModelDisabled(requestedModel, resolvedModelAliases))
            return;

        throw new NotSupportedException(ModelResolverOptions.GetDisabledModelMessage(requestedModel));
    }

    private IModelProvider ResolveProvider(string identifier)
        => providers.GetModelProvider(services, identifier)
            ?? throw new NotSupportedException($"Provider '{identifier}' is not available.");

    public IModelProvider GetProvider() => ResolveProvider(providers.ModelProviderIds
        .FirstOrDefault(id => !string.IsNullOrEmpty(apiKeyResolver.Resolve(id)))
        ?? providers.ModelProviderIds.FirstOrDefault(id => id == "pollinations")
        ?? throw new NotSupportedException("No providers found"));

    public async Task<ModelResponse> ResolveModels(CancellationToken ct)
    {
        var map = await GetAggregateMapAsync(ct);

        return new ModelResponse
        {
            Data = [..
                map.Values
                    .Select(v => v.Model)
                    .OrderByDescending(m => m.Created)]
        };
    }

    public async Task RefreshQueuedProviderAsync(ModelListingRefreshRequest request, CancellationToken ct)
    {
        var provider = providers.ModelProviderIds.FirstOrDefault(id => string.Equals(id, request.ProviderId, StringComparison.OrdinalIgnoreCase));
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
            provider,
            providerCacheKey,
            refreshedProviderSnapshot,
            ct);
        if (refreshed.ModelProviderMap.Count == 0)
            return;

        await SaveAggregateSnapshotAsync(refreshed, ct);
        memoryCache.Set(GetAggregateMemoryCacheKey(), refreshed, _options.MemoryCacheTtl);
    }

    private async Task<Dictionary<string, (Model Model, string ProviderId)>> GetAggregateMapAsync(CancellationToken ct)
    {
        var requestScopedProviders = GetSelectedProviders()
            .Where(provider => IsRequestScopedProvider(provider))
            .GroupBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var sharedMap = GetAggregateProviders().Any()
            ? await GetSharedAggregateMapAsync(ct)
            : new Dictionary<string, (Model Model, string ProviderId)>(StringComparer.OrdinalIgnoreCase);

        // Never add user-scoped agents to the shared memory entry or persisted snapshots.
        var merged = new Dictionary<string, (Model Model, string ProviderId)>(sharedMap, StringComparer.OrdinalIgnoreCase);
        foreach (var provider in requestScopedProviders)
        {
            try
            {
                foreach (var model in await ResolveProvider(provider).ListModels(ct))
                    merged[BuildModelIdentityKey(model)] = (model, provider);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Request-scoped model discovery failed for {ProviderId}.", provider);
            }
        }

        return merged;

    }
    private async Task<Dictionary<string, (Model Model, string ProviderId)>> GetSharedAggregateMapAsyncLive(
        CancellationToken ct)
    {
        var cacheKey = GetAggregateMemoryCacheKey();

        var response = await memoryCache.GetOrCreateAsync(
            cacheKey,
            BuildLiveAggregateWithoutStorageAsync,
            baseTtl: _options.MemoryCacheTtl,
            cancellationToken: ct);

        return response.ModelProviderMap;
    }

    private async Task<AggregateModelsCacheEntry> BuildLiveAggregateWithoutStorageAsync(
        CancellationToken ct)
    {
        var selectedProviders = GetAggregateProviders()
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        var merged =
            new ConcurrentDictionary<string, (Model Model, string ProviderId)>(
                StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            selectedProviders,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(
                    1,
                    Math.Min(selectedProviders.Length, _options.MaxParallelFirstLoad)),
                CancellationToken = ct
            },
            async (provider, token) =>
            {
                try
                {
                    var models = await ResolveProvider(provider).ListModels(token);

                    foreach (var model in models)
                        merged[BuildModelIdentityKey(model)] = (model, provider);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Live model discovery failed for {ProviderId}.",
                        provider);
                }
            });

        var result = merged.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.OrdinalIgnoreCase);

        await EnrichModelsAsync(result, ct);

        return new AggregateModelsCacheEntry(
            result,
            DateTimeOffset.UtcNow.Add(_options.MemoryCacheTtl),
            []);
    }

    private async Task<Dictionary<string, (Model Model, string ProviderId)>> GetSharedAggregateMapAsync(CancellationToken ct)
    {
        var aggregateCacheKey = GetAggregateMemoryCacheKey();

        var response = await memoryCache.GetOrCreateAsync(
            aggregateCacheKey,
            LoadAggregateResponseAsync,
            baseTtl: _options.MemoryCacheTtl,
            cancellationToken: ct);

        if (response.ModelProviderMap.Values.Any(entry => IsRequestScopedProvider(entry.ProviderId))
            || response.ProviderStates.Any(state => IsRequestScopedProvider(state.ProviderId)))
        {
            response = new AggregateModelsCacheEntry(
                response.ModelProviderMap
                    .Where(entry => !IsRequestScopedProvider(entry.Value.ProviderId))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                response.RefreshAfterUtc,
                [.. response.ProviderStates.Where(state => !IsRequestScopedProvider(state.ProviderId))]);
            memoryCache.Set(aggregateCacheKey, response, _options.MemoryCacheTtl);
        }

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
            if (!UseKeyedFirstProviderSelection)
            {
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

        if (!_options.IncludeApiKeysInSnapshotIdentity)
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

            var providerSnapshots = new Dictionary<string, StoredProviderModelSnapshot>(StringComparer.OrdinalIgnoreCase);
            var providerCandidates = GetAggregateProviders();

            var providerArray = providerCandidates
                .GroupBy(provider => provider, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(_ => Random.Shared.Next())
                .ToArray();

            if (UseKeyedFirstProviderSelection && providerArray.Length == 0)
            {
                logger.LogInformation(
                    "Header-auth model listing is active for {AggregateKey}. No request-keyed providers or anonymous providers were selected.",
                    BuildAggregateSnapshotKey());
            }

            await Parallel.ForEachAsync(
                providerArray,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(providerArray.Length, _options.MaxParallelFirstLoad)),
                    CancellationToken = ct
                },
                async (provider, token) =>
                {
                    baselineProviderStates.TryGetValue(provider, out var providerState);

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
            var providerSnapshots = new Dictionary<string, StoredProviderModelSnapshot>(StringComparer.OrdinalIgnoreCase);
            var refreshedProvider = providers.ModelProviderIds.FirstOrDefault(id =>
                string.Equals(id, refreshedProviderId, StringComparison.OrdinalIgnoreCase));

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
        string provider,
        string? sourceProviderCacheKey,
        CancellationToken ct)
    {
        if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
            return null;

        var snapshot = await LoadServableProviderSnapshotAsync(
            provider,
            providerCacheKey,
            sourceProviderCacheKey ?? providerCacheKey,
            queueRefreshIfStale: true,
            ct);

        if (snapshot != null)
            return snapshot;

        if (!ShouldPerformSynchronousProviderRefresh(provider))
        {
            await QueueProviderRefreshAsync(provider, providerCacheKey);
            return null;
        }

        return await RefreshProviderSnapshotAsync(provider, ct);
    }

    private async Task<StoredProviderModelSnapshot?> RefreshProviderSnapshotAsync(string provider, CancellationToken ct)
    {
        if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
            return null;

        try
        {
            var models = (await ResolveProvider(provider).ListModels(ct)).ToList();
            if (models.Count == 0)
                return null;

            var now = DateTimeOffset.UtcNow;
            var snapshot = new StoredProviderModelSnapshot
            {
                ProviderId = provider,
                CacheKey = providerCacheKey,
                StoredAtUtc = now,
                RefreshAfterUtc = now.Add(_options.ProviderRefreshAfter).AddMinutes(Random.Shared.Next(0, Math.Max(1, _options.ProviderRefreshJitterMinutes))),
                ExpiresAtUtc = now.Add(_options.ProviderSnapshotTtl),
                Models = models
            };

            await snapshotStore.SaveProviderSnapshotAsync(provider, providerCacheKey, snapshot, ct);
            refreshState.QueuedProviders.TryRemove(BuildQueuedProviderKey(provider, providerCacheKey), out _);
            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await LoadServableProviderSnapshotAsync(
                provider,
                providerCacheKey,
                providerCacheKey,
                queueRefreshIfStale: false,
                ct);
        }
    }

    private AggregateMergeResult MergeProviderSnapshots(
        Dictionary<string, StoredProviderModelSnapshot> snapshots,
        AggregateBaseline? baseline)
    {
        var merged = new Dictionary<string, (Model Model, string ProviderId)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, snapshot) in snapshots)
        {
            foreach (var model in snapshot.Models)
                merged[BuildModelIdentityKey(model)] = (model, provider);
        }

        if (baseline == null)
            return new AggregateMergeResult(merged, []);

        var refreshedProviderIds = snapshots.Keys
            .Select(provider => provider)
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

            var provider = providers.ModelProviderIds.FirstOrDefault(id => string.Equals(id, entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
                continue;

            merged.TryAdd(BuildModelIdentityKey(entry.Model), (entry.Model, provider));
        }

        return new AggregateMergeResult(merged, preservedProviderStates);
    }

    private async Task EnrichModelsAsync(Dictionary<string, (Model Model, string ProviderId)> merged, CancellationToken ct)
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
            var maxTokens = models.FirstOrDefault(m => m.MaxTokens != null && m.MaxTokens != 0)?.MaxTokens;
            var created = models.FirstOrDefault(m => m.Created != null)?.Created;
            //var tags = models.FirstOrDefault(m => m.Tags?.Any() == true)?.Tags;

            foreach (var model in models)
            {
                model.ContextWindow ??= contextWindow;
                model.MaxTokens ??= maxTokens;
                model.Created ??= created;
                //                model.Tags ??= tags;
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
                    entry.ModelProviderMap.Values
                    .Where(v => !IsRequestScopedProvider(v.ProviderId))
                    .Select(v => new StoredResolvedModelEntry
                    {
                        ProviderId = v.ProviderId,
                        Model = v.Model
                    })],
                Providers = [.. entry.ProviderStates.Where(state => !IsRequestScopedProvider(state.ProviderId))]
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
        var cacheKey = GetAggregateMemoryCacheKey();
        var state = refreshState;
        var scopes = scopeFactory;
        if (!state.AggregateRefreshes.TryAdd(cacheKey, 0))
            return;

        // Do not flow an HttpContext/AsyncLocal credential context into detached work,
        // or retain this resolver's scoped IServiceProvider past request disposal.
        using var flow = ExecutionContext.SuppressFlow();
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<StorageBackedModelProviderResolver>();
                // Request credentials cannot be recovered in a background scope. Never
                // publish a differently-selected or differently-keyed catalog as this one.
                if (!string.Equals(cacheKey, resolver.GetAggregateMemoryCacheKey(), StringComparison.Ordinal))
                    return;
                var entry = await resolver.BuildAggregateResponseAsync(CancellationToken.None);
                await resolver.SaveAggregateSnapshotAsync(entry, CancellationToken.None);
                resolver.PublishAggregate(entry);
            }
            catch
            {
                // background refresh is best effort
            }
            finally
            {
                state.AggregateRefreshes.TryRemove(cacheKey, out _);
            }
        });
    }

    private void TriggerBackgroundProviderRefresh(string providerId, string providerCacheKey)
    {
        if (IsRequestScopedProvider(providerId))
            return;

        // Enqueue uses shared infrastructure only, never a provider or scoped resolver.
        var queue = refreshQueue;
        var state = refreshState;
        var log = logger;
        var key = BuildQueuedProviderKey(providerId, providerCacheKey);
        if (!state.QueuedProviders.TryAdd(key, 0))
            return;
        using var flow = ExecutionContext.SuppressFlow();
        _ = Task.Run(async () =>
        {
            try
            {
                await queue.EnqueueAsync(new ModelListingRefreshRequest { ProviderId = providerId, CacheKey = providerCacheKey });
            }
            catch (Exception ex)
            {
                state.QueuedProviders.TryRemove(key, out _);
                log.LogWarning(
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
        if (IsRequestScopedProvider(providerId) || _options.IncludeApiKeysInSnapshotIdentity)
            return;

        var store = snapshotStore;
        var log = logger;
        using var flow = ExecutionContext.SuppressFlow();
        _ = Task.Run(async () =>
        {
            try
            {
                await store.SaveProviderSnapshotAsync(providerId, providerCacheKey, snapshot, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "Failed to backfill latest provider snapshot alias for {ProviderId} with cache key {CacheKey}.",
                    providerId,
                    providerCacheKey);
            }
        });
    }

    private async Task QueueProviderRefreshAsync(string providerId, string providerCacheKey)
    {
        if (IsRequestScopedProvider(providerId))
            return;

        var dedupeKey = BuildQueuedProviderKey(providerId, providerCacheKey);
        if (!refreshState.QueuedProviders.TryAdd(dedupeKey, 0))
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
            refreshState.QueuedProviders.TryRemove(dedupeKey, out _);
            TriggerBackgroundAggregateRefresh();
        }
    }

    private Dictionary<string, (Model Model, string ProviderId)> RestoreSnapshot(StoredResolvedModelSnapshot snapshot)
    {
        var map = new Dictionary<string, (Model Model, string ProviderId)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot.Entries)
        {
            if (IsRequestScopedProvider(entry.ProviderId))
                continue;

            var provider = providers.ModelProviderIds.FirstOrDefault(id => string.Equals(id, entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider == null || string.IsNullOrWhiteSpace(entry.Model.Id))
                continue;

            map[BuildModelIdentityKey(entry.Model)] = (entry.Model, provider);
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

        // Legacy aggregates may contain agents from another user's discovery scope.
        snapshot = new StoredResolvedModelSnapshot
        {
            AggregateKey = snapshot.AggregateKey,
            StoredAtUtc = snapshot.StoredAtUtc,
            RefreshAfterUtc = snapshot.RefreshAfterUtc,
            ExpiresAtUtc = snapshot.ExpiresAtUtc,
            Entries = [.. snapshot.Entries.Where(entry => !IsRequestScopedProvider(entry.ProviderId))],
            Providers = [.. snapshot.Providers.Where(state => !IsRequestScopedProvider(state.ProviderId))]
        };

        if (UseKeyedFirstProviderSelection)
            snapshot = FilterSnapshotForCurrentRequest(snapshot);

        if (snapshot == null || snapshot.Entries.Count == 0)
            return null;

        var restored = RestoreSnapshot(snapshot);
        if (restored.Count == 0)
            return null;

        return new AggregateBaseline(snapshot, restored);
    }

    private StoredResolvedModelSnapshot? FilterSnapshotForCurrentRequest(StoredResolvedModelSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var requestProviders = GetAggregateProviders()
            .GroupBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(provider => provider, provider => provider, StringComparer.OrdinalIgnoreCase);

        var latestStates = snapshot.Providers
            .Where(state => state.ExpiresAtUtc > now)
            .Where(state => requestProviders.ContainsKey(state.ProviderId))
            .GroupBy(state => state.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(state => state.StoredAtUtc).First())
            .ToList();

        var validProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in latestStates)
        {
            if (!requestProviders.TryGetValue(state.ProviderId, out var provider))
                continue;

            if (provider == null || !TryGetProviderCacheKey(provider, out var expectedCacheKey))
                continue;

            var stateCacheKey = state.SourceCacheKey ?? state.CacheKey;
            if (string.Equals(stateCacheKey, expectedCacheKey, StringComparison.Ordinal))
            {
                validProviderIds.Add(state.ProviderId);
                continue;
            }
        }

        if (validProviderIds.Count == 0)
            return null;

        var filteredProviders = latestStates
            .Where(state => validProviderIds.Contains(state.ProviderId))
            .ToList();

        var filteredEntries = snapshot.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Model.Id))
            .Where(entry => validProviderIds.Contains(entry.ProviderId))
            .ToList();

        if (filteredEntries.Count == 0)
            return null;

        return new StoredResolvedModelSnapshot
        {
            AggregateKey = snapshot.AggregateKey,
            StoredAtUtc = snapshot.StoredAtUtc,
            RefreshAfterUtc = snapshot.RefreshAfterUtc,
            ExpiresAtUtc = snapshot.ExpiresAtUtc,
            Entries = [.. filteredEntries],
            Providers = [.. filteredProviders]
        };
    }

    private AggregateModelsCacheEntry CreateCacheEntryFromBaseline(
        AggregateBaseline baseline,
        DateTimeOffset refreshAfterUtc)
        => new(
            new Dictionary<string, (Model Model, string ProviderId)>(baseline.ModelProviderMap, StringComparer.OrdinalIgnoreCase),
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
            .Where(provider => !knownProviderIds.Contains(provider))
            .GroupBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (missingProviders.Length == 0)
            return null;

        var recoveredSnapshots = new Dictionary<string, StoredProviderModelSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in missingProviders)
        {
            if (!TryGetProviderCacheKey(provider, out var providerCacheKey))
                continue;

            var snapshot = await LoadServableProviderSnapshotAsync(
                provider,
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
                    provider);

                continue;
            }

            recoveredSnapshots[provider] = snapshot;
        }

        if (recoveredSnapshots.Count == 0)
            return null;

        var merged = new Dictionary<string, (Model Model, string ProviderId)>(baseline.ModelProviderMap, StringComparer.OrdinalIgnoreCase);
        var originalModelCount = merged.Count;

        foreach (var (provider, snapshot) in recoveredSnapshots)
        {
            foreach (var model in snapshot.Models)
                merged[BuildModelIdentityKey(model)] = (model, provider);
        }

        await EnrichModelsAsync(merged, ct);

        var reconciled = new AggregateModelsCacheEntry(
            merged,
            refreshAfterUtc,
            BuildProviderStates(recoveredSnapshots, [.. NormalizeProviderStates(baseline.Snapshot.Providers)]));

        logger.LogWarning(
            "Aggregate snapshot {AggregateKey} was missing configured providers {ProviderIds}. Recovered {AddedModelCount} additional models from provider snapshots and repaired the aggregate.",
            baseline.Snapshot.AggregateKey,
            string.Join(", ", recoveredSnapshots.Keys.Select(provider => provider).OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)),
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
        if (IsRequestScopedProvider(providerId))
            return null;

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
        Dictionary<string, StoredProviderModelSnapshot> providerSnapshots,
        IReadOnlyCollection<StoredResolvedProviderState>? preservedProviderStates = null)
    {
        var states = providerSnapshots
            .Select(kvp => new StoredResolvedProviderState
            {
                ProviderId = kvp.Key,
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
                    return provider;

                var key = apiKeyResolver.Resolve(provider);
                var keyHash = string.IsNullOrWhiteSpace(key)
                    ? "nokey"
                    : ModelProviderExtensions.CacheKeyFromApiKey(key);

                return $"{provider}:{keyHash}";
            })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        var raw = string.Join("|", parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"resolver:{Convert.ToHexString(hash)}";
    }

    private string GetAggregateMemoryCacheKey() => AggregateMemoryCachePrefix + BuildAggregateSnapshotKey();

    private IEnumerable<string> GetConfiguredProviders() => providers.ModelProviderIds;

    private IEnumerable<string> GetAggregateProviders()
        => GetSelectedProviders().Where(provider => !IsRequestScopedProvider(provider));

    private static bool IsRequestScopedProvider(string providerId)
        => string.Equals(providerId, "m8tes", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> GetSelectedProviders()
        => UseKeyedFirstProviderSelection
            ? GetKeyedFirstProviders()
            : GetConfiguredProviders();

    private IEnumerable<string> GetKeyedFirstProviders()
    {
        var configuredProviders = GetConfiguredProviders().ToArray();
        var requestKeyedProviders = configuredProviders
            .Where(HasRequestProviderKey)
            .ToArray();

        if (requestKeyedProviders.Length > 0)
            return requestKeyedProviders
                .GroupBy(provider => provider, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());

        return configuredProviders
            .Where(provider => _alwaysIncludeProviders.Contains(provider))
            .GroupBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private bool UseKeyedFirstProviderSelection
        => _apiKeyPresenceResolver != null;

    private bool HasRequestProviderKey(string provider)
        => _apiKeyPresenceResolver?.HasConfiguredKey(provider) == true;

    private bool ShouldPerformSynchronousProviderRefresh(string provider)
    {
        if (!UseKeyedFirstProviderSelection)
            return true;

        return HasRequestProviderKey(provider)
               || !GetConfiguredProviders().Any(HasRequestProviderKey);
    }

    private bool TryGetProviderCacheKey(string provider, out string cacheKey)
    {
        if (IsRequestScopedProvider(provider))
        {
            cacheKey = string.Empty;
            return false;
        }

        var apiKey = _options.IncludeApiKeysInSnapshotIdentity
            ? apiKeyResolver.Resolve(provider)
            : null;

        cacheKey = string.IsNullOrWhiteSpace(apiKey)
            ? $"models:{provider}"
            : $"models:{provider}:{ModelProviderExtensions.CacheKeyFromApiKey(apiKey)}";
        return true;
    }

    private IEnumerable<StoredResolvedProviderState> NormalizeProviderStates(IEnumerable<StoredResolvedProviderState> states)
    {
        foreach (var state in states
                     .Where(state => !IsRequestScopedProvider(state.ProviderId))
                     .GroupBy(state => state.ProviderId, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(state => state.StoredAtUtc).First()))
        {
            if (_options.IncludeApiKeysInSnapshotIdentity)
            {
                yield return state;
                continue;
            }

            var provider = providers.ModelProviderIds.FirstOrDefault(id => string.Equals(id, state.ProviderId, StringComparison.OrdinalIgnoreCase));
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

    private void PublishAggregate(AggregateModelsCacheEntry entry)
        => memoryCache.Set(GetAggregateMemoryCacheKey(), entry, _options.MemoryCacheTtl);

    private static string BuildModelIdentityKey(Model model)
    {
        var id = model.Id.Trim();
        var type = string.IsNullOrWhiteSpace(model.Type)
            ? id.GuessModelType()
            : model.Type.Trim();

        model.Type = type;
        return $"{id.Length}:{id}{type.Length}:{type}";
    }

    private sealed record AggregateModelsCacheEntry(
        Dictionary<string, (Model Model, string ProviderId)> ModelProviderMap,
        DateTimeOffset RefreshAfterUtc,
        IReadOnlyList<StoredResolvedProviderState> ProviderStates);

    private sealed record AggregateBaseline(
        StoredResolvedModelSnapshot Snapshot,
        Dictionary<string, (Model Model, string ProviderId)> ModelProviderMap);

    private sealed record AggregateMergeResult(
        Dictionary<string, (Model Model, string ProviderId)> ModelProviderMap,
        IReadOnlyList<StoredResolvedProviderState> PreservedProviderStates);
}
