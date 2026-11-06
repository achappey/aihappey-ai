using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Orchestration;

public class ModelProviderResolver(
    IApiKeyResolver apiKeyResolver,
    ProviderRegistry providers,
    IServiceProvider services,
    AsyncCacheHelper memoryCache,
    IOptions<ModelListingOptions> options,
    IOptions<ModelResolverOptions> resolverOptions,
    ILogger<ModelProviderResolver> logger) : IAIModelProviderResolver
{
    private readonly ModelListingOptions _options = options.Value;
    private readonly ModelResolverOptions _resolverOptions = resolverOptions.Value;
    private readonly IApiKeyPresenceResolver? _apiKeyPresenceResolver = apiKeyResolver as IApiKeyPresenceResolver;
    private readonly HashSet<string> _alwaysIncludeProviders = (options.Value.AlwaysIncludeProviders ?? [])
        .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
        .Select(providerId => providerId.Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Qualified execution discovers only its provider, never the entire catalog.
        if (model.Contains('/', StringComparison.Ordinal)
            && providers.HasModelProvider(model.SplitModelId().Provider))
        {
            var providerId = GetSelectedProviders().FirstOrDefault(id =>
                string.Equals(id, model.SplitModelId().Provider, StringComparison.OrdinalIgnoreCase));
            if (providerId == null)
                throw new ModelProviderNotFoundException(model);

            IEnumerable<Model> models;
            if (!IsRequestScopedProvider(providerId)
                && memoryCache.TryGetValue<Dictionary<string, (Model Model, string ProviderId)>>(
                    GetAggregateMemoryCacheKey(), out var cached)
                && cached!.Values.Any(entry =>
                    string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)))
            {
                models = cached.Values
                    .Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Model);
            }
            else
            {
                try
                {
                    models = await ResolveProvider(providerId).ListModels(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (!IsRequestScopedProvider(providerId))
                {
                    logger.LogWarning(ex, "Live model discovery failed for {ProviderId}.", providerId);
                    models = [];
                }
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
        if (_resolverOptions.IsModelDisabled(requestedModel, resolvedModelAliases))
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
            Data = [.. map.Values.Select(v => v.Model).OrderByDescending(m => m.Created)]
        };
    }

    private async Task<Dictionary<string, (Model Model, string ProviderId)>> GetAggregateMapAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selectedProviders = GetSelectedProviders().ToArray();
        var sharedMap = selectedProviders.Any(provider => !IsRequestScopedProvider(provider))
            ? await memoryCache.GetOrCreateAsync(
                GetAggregateMemoryCacheKey(),
                BuildAggregateAsync,
                baseTtl: _options.MemoryCacheTtl,
                cancellationToken: ct)
            : new Dictionary<string, (Model Model, string ProviderId)>(StringComparer.OrdinalIgnoreCase);

        // User-scoped agents must never enter the shared memory entry, even when
        // two request scopes use the same provider credential.
        var merged = new Dictionary<string, (Model Model, string ProviderId)>(sharedMap, StringComparer.OrdinalIgnoreCase);
        foreach (var provider in selectedProviders.Where(IsRequestScopedProvider))
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

    private async Task<Dictionary<string, (Model Model, string ProviderId)>> BuildAggregateAsync(CancellationToken ct)
    {
        var selectedProviders = GetSelectedProviders().Where(provider => !IsRequestScopedProvider(provider)).ToArray();
        var merged = new ConcurrentDictionary<string, (Model Model, string ProviderId)>(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            selectedProviders,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(selectedProviders.Length, _options.MaxParallelFirstLoad)),
                CancellationToken = ct
            },
            async (provider, token) =>
            {
                try
                {
                    foreach (var model in await ResolveProvider(provider).ListModels(token))
                        merged[BuildModelIdentityKey(model)] = (model, provider);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Live model discovery failed for {ProviderId}.", provider);
                }
            });

        var result = merged.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        EnrichModels(result);
        return result;
    }

    private static void EnrichModels(Dictionary<string, (Model Model, string ProviderId)> merged)
    {
        var modelsByBase = merged.Values
            .Select(v => v.Model)
            .GroupBy(m => m.Id.Split('/').Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in modelsByBase)
        {
            var models = group.ToList();
            var contextWindow = models.FirstOrDefault(m => m.ContextWindow != null)?.ContextWindow;
            var maxTokens = models.FirstOrDefault(m => m.MaxTokens != null && m.MaxTokens != 0)?.MaxTokens;
            var created = models.FirstOrDefault(m => m.Created != null)?.Created;

            foreach (var model in models)
            {
                model.ContextWindow ??= contextWindow;
                model.MaxTokens ??= maxTokens;
                model.Created ??= created;
            }
        }
    }

    private string GetAggregateMemoryCacheKey()
    {
        var parts = GetSelectedProviders()
            .Where(provider => !IsRequestScopedProvider(provider))
            .Select(provider =>
            {
                var key = apiKeyResolver.Resolve(provider);
                var keyHash = string.IsNullOrWhiteSpace(key)
                    ? "nokey"
                    : ModelProviderExtensions.CacheKeyFromApiKey(key);
                return $"{provider}:{keyHash}";
            })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return $"resolver:aggregate:{Convert.ToHexString(hash)}";
    }

    private static bool IsRequestScopedProvider(string providerId)
        => string.Equals(providerId, "m8tes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, "baguettebot", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> GetSelectedProviders()
    {
        var configuredProviders = providers.ModelProviderIds.ToArray();
        if (_apiKeyPresenceResolver == null)
            return configuredProviders;

        var keyedProviders = configuredProviders
            .Where(provider => _apiKeyPresenceResolver.HasConfiguredKey(provider))
            .ToArray();

        return (keyedProviders.Length > 0
                ? keyedProviders
                : configuredProviders.Where(provider => _alwaysIncludeProviders.Contains(provider)))
            .GroupBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private static string BuildModelIdentityKey(Model model)
    {
        var id = model.Id.Trim();
        var type = string.IsNullOrWhiteSpace(model.Type) ? id.GuessModelType() : model.Type.Trim();
        model.Type = type;
        return $"{id.Length}:{id}{type.Length}:{type}";
    }
}
