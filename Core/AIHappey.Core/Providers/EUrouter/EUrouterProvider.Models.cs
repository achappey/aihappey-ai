using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    private const string ModelsRawCacheSuffix = ":raw";
    private const string ResponsesEndpointName = "responses";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var listing = await GetModelsListingAsync(cancellationToken);
        return listing.Models;
    }

    private async Task<EUrouterModelsListing> GetModelsListingAsync(CancellationToken cancellationToken = default)
    {
        var rawModels = await GetRawModelsAsync(cancellationToken);
        return CreateModelsListing(rawModels);
    }

    private async Task<IReadOnlyList<JsonElement>> GetRawModelsAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = GetRawModelsCacheKey();

        try
        {
            return await _memoryCache.GetOrCreateAsync(
                cacheKey,
                FetchRawModelsAsync,
                baseTtl: TimeSpan.FromHours(4),
                jitterMinutes: 480,
                cancellationToken: cancellationToken);
        }
        catch
        {
            return await FetchRawModelsAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<JsonElement>> FetchRawModelsAsync(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"EUrouter API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
            return [];

        return [.. dataEl.EnumerateArray().Select(el => el.Clone())];
    }

    private static EUrouterModelsListing CreateModelsListing(IReadOnlyList<JsonElement> rawModels)
    {
        var models = new List<Model>();
        var originalByModelId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var originalByRequestModelId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in rawModels)
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var modelId = idEl.GetString();
            if (string.IsNullOrEmpty(modelId))
                continue;

            originalByModelId[modelId] = el;
            originalByRequestModelId[modelId] = el;
            originalByRequestModelId[$"eurouter/{modelId}"] = el;

            string name = modelId;

            if (el.TryGetProperty("name", out var nameEl))
                name = nameEl.GetString() ?? name;

            string description = string.Empty;

            if (el.TryGetProperty("description", out var descriptionEl))
                description = descriptionEl.GetString() ?? string.Empty;

            int? contextLength = null;
            if (el.TryGetProperty("context_length", out var ctxEl) &&
                ctxEl.ValueKind == JsonValueKind.Number)
            {
                contextLength = ctxEl.GetInt32();
            }

            decimal? inputPrice = null;
            decimal? inputCacheReadPrice = null;
            decimal? inputCacheWritePrice = null;
            decimal? outputPrice = null;

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                if (pricingEl.TryGetProperty("prompt", out var inEl))
                {
                    if (decimal.TryParse(inEl.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                        inputPrice = parsed;
                }

                if (pricingEl.TryGetProperty("completion", out var outEl))
                {
                    if (decimal.TryParse(outEl.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                        outputPrice = parsed;
                }

                if (pricingEl.TryGetProperty("input_cache_read", out var inputCacheReadEl))
                {
                    if (decimal.TryParse(inputCacheReadEl.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                        inputCacheReadPrice = parsed;
                }

                if (pricingEl.TryGetProperty("input_cache_write", out var inputCacheWriteEl))
                {
                    if (decimal.TryParse(inputCacheWriteEl.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                        inputCacheWritePrice = parsed;
                }

            }

            // IMPORTANT PART:
            // one EUrouter model can have multiple providers
            if (el.TryGetProperty("providers", out var providersEl) &&
                providersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var providerEl in providersEl.EnumerateArray())
                {
                    if (!providerEl.TryGetProperty("slug", out var slugEl))
                        continue;

                    var providerSlug = slugEl.GetString();
                    if (string.IsNullOrEmpty(providerSlug))
                        continue;

                    var providerModelName = providerEl.TryGetProperty("name", out var providerNameEl) ?
                        $"{name} (${providerNameEl.GetString()})"
                        : $"{name} (${providerSlug})";

                    var exposedModelId = $"eurouter/{providerSlug}/{modelId}";
                    originalByRequestModelId[exposedModelId] = el;
                    originalByRequestModelId[$"{providerSlug}/{modelId}"] = el;

                    var model = new Model
                    {
                        Id = exposedModelId,
                        Name = providerModelName,
                        OwnedBy = providerSlug,
                        Description = description,
                        ContextWindow = contextLength,
                    };

                    if (inputPrice.HasValue && outputPrice.HasValue &&
                        inputPrice.Value > 0 && outputPrice.Value > 0)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = inputPrice.Value,
                            Output = outputPrice.Value,
                            InputCacheRead = inputCacheReadPrice > 0 ? inputCacheReadPrice : null,
                            InputCacheWrite = inputCacheWritePrice > 0 ? inputCacheWritePrice : null
                        };
                    }

                    models.Add(model);
                }
            }
        }

        return new EUrouterModelsListing(
            models,
            originalByModelId,
            originalByRequestModelId);
    }

    private async Task<JsonElement?> GetOriginalModelDataAsync(string? requestModel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestModel))
            return null;

        var listing = await GetModelsListingAsync(cancellationToken);
        var candidates = GetModelLookupCandidates(requestModel);

        foreach (var candidate in candidates)
        {
            if (listing.OriginalByRequestModelId.TryGetValue(candidate, out var original))
                return original;

            if (listing.OriginalByModelId.TryGetValue(candidate, out original))
                return original;
        }

        return null;
    }

    private async Task<bool> SupportsResponsesEndpointAsync(string? requestModel, CancellationToken cancellationToken = default)
    {
        try
        {
            var originalModel = await GetOriginalModelDataAsync(requestModel, cancellationToken);
            return SupportsResponsesEndpoint(originalModel);
        }
        catch
        {
            return false;
        }
    }

    public static bool SupportsResponsesEndpoint(JsonElement? originalModel)
    {
        if (originalModel is null || originalModel.Value.ValueKind != JsonValueKind.Object)
            return false;

        if (!originalModel.Value.TryGetProperty("supported_api_endpoints", out var endpointsEl) ||
            endpointsEl.ValueKind != JsonValueKind.Array)
            return false;

        return endpointsEl.EnumerateArray().Any(endpoint =>
            endpoint.ValueKind == JsonValueKind.String &&
            string.Equals(endpoint.GetString(), ResponsesEndpointName, StringComparison.OrdinalIgnoreCase));
    }

    private string GetRawModelsCacheKey()
        => this.GetCacheKey() + ModelsRawCacheSuffix;

    private static IEnumerable<string> GetModelLookupCandidates(string requestModel)
    {
        yield return requestModel;

        if (!requestModel.Contains('/'))
            yield break;

        var split = requestModel.SplitModelId();
        if (!string.IsNullOrWhiteSpace(split.Model))
            yield return split.Model;

        if (string.Equals(split.Provider, "eurouter", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(split.Model))
        {
            yield return $"eurouter/{split.Model}";

            if (split.Model.Contains('/'))
            {
                var routedModel = split.Model.SplitModelId();
                if (!string.IsNullOrWhiteSpace(routedModel.Model))
                    yield return routedModel.Model;
            }
        }
    }

    private sealed record EUrouterModelsListing(
        IReadOnlyList<Model> Models,
        IReadOnlyDictionary<string, JsonElement> OriginalByModelId,
        IReadOnlyDictionary<string, JsonElement> OriginalByRequestModelId);
}
