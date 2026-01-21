using System.Net.Http.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.ModelProviders;

public class CachedModelProviderResolver(IApiKeyResolver apiKeyResolver,
    IEnumerable<IModelProvider> providers,
    IHttpClientFactory httpClientFactory) : IAIModelProviderResolver
{
    private Dictionary<string, (Model Model, IModelProvider Provider)>? _modelProviderMap;

    private DateTimeOffset _expiresAt;

    private Dictionary<string, (Model Model, IModelProvider Provider)>? _lastKnownGood;

    private readonly TimeSpan _slidingExpiration = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private async Task<IEnumerable<Model>?> FetchVercelModels(CancellationToken ct)
    {
        var _http = httpClientFactory.CreateClient();

        try
        {
            var json = await _http.GetFromJsonAsync<ModelResponse>(
                "https://ai-gateway.vercel.sh/v1/models", ct);

            return json?.Data;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task EnsureModelsLoaded(CancellationToken ct)
    {
        if (_modelProviderMap != null && DateTimeOffset.UtcNow < _expiresAt)
            return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // double-check after lock
            if (_modelProviderMap != null && DateTimeOffset.UtcNow < _expiresAt)
                return;

            var providerArray = providers as IModelProvider[] ?? [.. providers];

            var tasks = providerArray.Select(async provider =>
            {
                try
                {
                    var models = await provider.ListModels(ct);
                    return (Provider: provider, Models: models?.ToList());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // provider temporarily down → ignore
                    return (Provider: provider, Models: (List<Model>?)null);
                }
            });

            var resolved = await Task.WhenAll(tasks);

            var providerResults = new Dictionary<IModelProvider, List<Model>>();

            foreach (var r in resolved)
            {
                if (r.Models is { Count: > 0 })
                    providerResults[r.Provider] = r.Models;
            }



            var hasAnyModels = providerResults.Values.Any(v => v.Count > 0);

            if (!hasAnyModels)
            {
                if (_lastKnownGood != null)
                {
                    _modelProviderMap = _lastKnownGood;
                    _expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
                    return;
                }

                throw new InvalidOperationException("No models resolved from any provider.");
            }

            var merged = new Dictionary<string, (Model Model, IModelProvider Provider)>(
                _lastKnownGood ?? [],
                StringComparer.OrdinalIgnoreCase
            );

            // Remove old entries ONLY for providers that successfully refreshed
            foreach (var provider in providerResults.Keys)
            {
                foreach (var modelId in merged
                    .Where(kvp => kvp.Value.Provider == provider)
                    .Select(kvp => kvp.Key)
                    .ToList())
                {
                    merged.Remove(modelId);
                }
            }

            // Add fresh models
            foreach (var (provider, models) in providerResults)
            {
                foreach (var model in models)
                {
                    merged[model.Id] = (model, provider);
                }
            }

            var vercelModels = await FetchVercelModels(ct);

            foreach (var key in merged.Keys.ToList())
            {
                var enrich = vercelModels?
                    .FirstOrDefault(v =>
                        key.EndsWith(v.Id, StringComparison.OrdinalIgnoreCase));

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
            }


            if (merged.Count == 0)
                throw new InvalidOperationException("No models resolved.");

            _modelProviderMap = merged;
            _lastKnownGood = merged;
            _expiresAt = DateTimeOffset.UtcNow.Add(_slidingExpiration);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<IModelProvider> Resolve(
       string model,
       CancellationToken ct = default)
    {
        await EnsureModelsLoaded(ct);

        if (_modelProviderMap!.TryGetValue(model, out var entry))
            return entry.Provider;

        var key = _modelProviderMap.Keys
            .FirstOrDefault(z => z.SplitModelId().Model == model);

        if (key != null && _modelProviderMap!.TryGetValue(key, out var backEntry))
            return backEntry.Provider;

        throw new NotSupportedException($"No provider found for model '{model}'.");
    }

    public IModelProvider GetProvider() => providers
            .FirstOrDefault(p => !string.IsNullOrEmpty(apiKeyResolver.Resolve(p.GetIdentifier())))
            ?? providers.FirstOrDefault(a => a.GetIdentifier() == "pollinations")
            ?? throw new NotSupportedException($"No providers found");

    public async Task<ModelResponse> ResolveModels(CancellationToken ct)
    {
        await EnsureModelsLoaded(ct);

        return new()
        {
            Data = [.. _modelProviderMap!
                .Values
                .Select(v => v.Model)
                .Where(a => a.Type != "embedding")
                .Where(a => a.Type != "rerank")
                .OrderByDescending(m => m.Created)]
        };
    }
}
