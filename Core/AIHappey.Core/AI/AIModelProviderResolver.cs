using System.Net.Http.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public class AIModelProviderResolver(
    IApiKeyResolver apiKeyResolver,
    IEnumerable<IModelProvider> providers,
    IHttpClientFactory httpClientFactory) : IAIModelProviderResolver
{
    private async Task<IEnumerable<Model>?> FetchVercelModels(CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient();
            var json = await http.GetFromJsonAsync<ModelReponse>(
                "https://ai-gateway.vercel.sh/v1/models", ct);

            return json?.Data;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Dictionary<string, (Model Model, IModelProvider Provider)>> LoadModels(
        CancellationToken ct)
    {
        var result = new Dictionary<string, (Model, IModelProvider)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            try
            {
                var models = await provider.ListModels(ct);
                foreach (var model in models)
                    result[model.Id] = (model, provider);
            }
            catch
            {
                // provider down → skip
            }
        }

        if (result.Count == 0)
            throw new InvalidOperationException("No models resolved from any provider.");

        var vercelModels = await FetchVercelModels(ct);

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

    public async Task<IModelProvider> Resolve(
        string model,
        CancellationToken ct = default)
    {
        var map = await LoadModels(ct);

        if (map.TryGetValue(model, out var entry))
            return entry.Provider;

        var key = map.Keys
            .FirstOrDefault(k => k.SplitModelId().Model == model);

        if (key != null && map.TryGetValue(key, out var backEntry))
            return backEntry.Provider;

        throw new NotSupportedException($"No provider found for model '{model}'.");
    }

    public IModelProvider GetProvider() =>
        providers
            .Where(p => !string.IsNullOrEmpty(apiKeyResolver.Resolve(p.GetIdentifier())))
            .OrderByDescending(p => p.GetPriority() ?? 0f)
            .FirstOrDefault()
        ?? providers.FirstOrDefault(p => p.GetIdentifier() == "pollinations")
        ?? throw new NotSupportedException("No providers found.");

    public async Task<ModelReponse> ResolveModels(CancellationToken ct)
    {
        var map = await LoadModels(ct);

        return new()
        {
            Data = map.Values
                .Select(v => v.Model)
                .Where(m => m.Type != "embedding" && m.Type != "rerank")
                .OrderByDescending(m => m.Created)
                .ToList()
        };
    }
}
