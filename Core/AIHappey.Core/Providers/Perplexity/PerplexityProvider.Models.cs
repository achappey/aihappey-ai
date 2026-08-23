using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var agentModels = await GetModelsAsync("v1/models", "agent", ct);
                //Router API is in private preview, so disabled for now 
                //    var routerModels = await GetModelsAsync("router/v1/models", "router", ct);

                var staticAgentModels = GetIdentifier().GetModels()
                    .Select(model => PrefixModel(model, "agent"));

                return staticAgentModels
                    .Concat(agentModels)
                //    .Concat(routerModels)
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<List<Model>> GetModelsAsync(
        string relativeUrl,
        string route,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Perplexity {route} model listing failed with status {(int)resp.StatusCode}: {err}",
                null,
                resp.StatusCode);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Perplexity {route} model listing did not contain a data array.");

        var models = new List<Model>();
        foreach (var element in data.EnumerateArray())
        {
            var upstreamId = element.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(upstreamId))
                continue;

            var model = new Model
            {
                Id = $"{route}/{upstreamId}".ToModelId(GetIdentifier()),
                Name = upstreamId.Split('/').LastOrDefault() ?? upstreamId,
                OwnedBy = element.TryGetProperty("owned_by", out var owner) ? owner.GetString() ?? string.Empty : string.Empty,
                Created = element.TryGetProperty("created", out var created) && created.TryGetInt64(out var createdValue)
                    ? createdValue
                    : null,
                Type = "language",
                Tags = route.Equals("agent") ? [route] : [],
                Description = route.Equals("agent") ? "Perplexity Agent" : string.Empty,
                Pricing = route == "router" ? ParseRouterPricing(element) : null
            };

            models.Add(model);
        }

        return models;
    }

    private Model PrefixModel(Model source, string route)
    {
        var providerPrefix = GetIdentifier() + "/";
        var upstreamId = source.Id.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? source.Id[providerPrefix.Length..]
            : source.Id;

        return new Model
        {
            Id = $"{route}/{upstreamId}".ToModelId(GetIdentifier()),
            Object = source.Object,
            Created = source.Created,
            OwnedBy = source.OwnedBy,
            Name = source.Name,
            Description = source.Description,
            ContextWindow = source.ContextWindow,
            MaxTokens = source.MaxTokens,
            Type = source.Type,
            Tags = [route],
            Pricing = source.Pricing
        };
    }

    private static ModelPricing? ParseRouterPricing(JsonElement model)
    {
        if (!model.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetDecimal(pricing, "input", out var input) || !TryGetDecimal(pricing, "output", out var output))
            return null;

        return new ModelPricing
        {
            Input = input,
            Output = output,
            InputCacheWrite = TryGetDecimal(pricing, "cache_write", out var cacheWrite) ? cacheWrite : null,
            InputCacheRead = TryGetDecimal(pricing, "cache_read", out var cacheRead) ? cacheRead : null
        };
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out value);
    }
}
