using System.Net.Http.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.ModelProviders;

public class AIModelProviderResolver(
    IApiKeyResolver apiKeyResolver,
    IEnumerable<IModelProvider> providers,
    IHttpClientFactory httpClientFactory) : IAIModelProviderResolver
{
    // ---- Vercel models cache (in-memory) ----
    private readonly SemaphoreSlim _vercelCacheLock = new(1, 1);
    private IReadOnlyList<Model>? _vercelModelsCache;
    private DateTimeOffset _vercelModelsCacheExpiresAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan VercelCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan VercelNegativeCacheTtl = TimeSpan.FromMinutes(5);

    private async Task<IReadOnlyList<Model>?> FetchVercelModels(CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient();
            var json = await http.GetFromJsonAsync<ModelResponse>(
                "https://ai-gateway.vercel.sh/v1/models", ct);

            return json?.Data?.ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<Model>?> GetVercelModelsCached(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (_vercelModelsCache != null && now < _vercelModelsCacheExpiresAt)
            return _vercelModelsCache;

        await _vercelCacheLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;

            if (_vercelModelsCache != null && now < _vercelModelsCacheExpiresAt)
                return _vercelModelsCache;

            var fresh = await FetchVercelModels(ct);

            if (fresh is { Count: > 0 })
            {
                _vercelModelsCache = fresh;
                _vercelModelsCacheExpiresAt = now.Add(VercelCacheTtl);
            }
            else
            {
                // negative-cache so we don't spam Vercel when it's down
                _vercelModelsCache ??= null;
                _vercelModelsCacheExpiresAt = now.Add(VercelNegativeCacheTtl);
            }

            return _vercelModelsCache;
        }
        finally
        {
            _vercelCacheLock.Release();
        }
    }

    private async Task<Dictionary<string, (Model Model, IModelProvider Provider)>> LoadModels(
        CancellationToken ct)
    {
        var result = new Dictionary<string, (Model, IModelProvider)>(StringComparer.OrdinalIgnoreCase);

        // ---- 1) resolve all provider models SEQUENTIALLY ----
        var providerArray = providers as IModelProvider[] ?? [.. providers];

        foreach (var provider in providerArray)
        {
            IEnumerable<Model> models;

            try
            {
                models = await provider.ListModels(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // provider down → skip
                models = Enumerable.Empty<Model>();
            }

            foreach (var model in models)
                result[model.Id] = (model, provider);
        }

        if (result.Count == 0)
            throw new InvalidOperationException("No models resolved from any provider.");

        // ---- 2) Vercel enrichment via cached lookup ----
        var vercelModels = await GetVercelModelsCached(ct);

        foreach (var key in result.Keys.ToList())
        {
            var (model, _) = result[key];

            model.Type ??= model.Id.GuessModelType() ?? string.Empty;

            var enrich = vercelModels?
                .FirstOrDefault(v => key.EndsWith(v.Id, StringComparison.OrdinalIgnoreCase));

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

        return result;
    }

    public async Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
    {
        var provKey = model.SplitModelId().Provider;
        var currentProv = providers.FirstOrDefault(a => a.GetIdentifier() == provKey
            && (provKey == "pollinations" || provKey == "echo" || provKey == "gtranslate"
                || !string.IsNullOrEmpty(apiKeyResolver.Resolve(provKey))));

        if (currentProv != null)
            return currentProv;

        throw new NotSupportedException($"No provider found for model '{model}'.");
    }

    public IModelProvider GetProvider() =>
        providers
            .FirstOrDefault(p => !string.IsNullOrEmpty(apiKeyResolver.Resolve(p.GetIdentifier())))
        ?? providers.FirstOrDefault(p => p.GetIdentifier() == "pollinations")
        ?? throw new NotSupportedException("No providers found.");

    public async Task<ModelResponse> ResolveModels(CancellationToken ct)
    {
        var map = await LoadModels(ct);

        return new()
        {
            Data = [.. map.Values
                .Select(v => v.Model)
                .Where(m => m.Type != "embedding" && m.Type != "rerank")
                .OrderByDescending(m => m.Created)]
        };
    }
}
