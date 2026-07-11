using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Venice API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array)
                    return models;

                foreach (var el in dataEl.EnumerateArray())
                {
                    var model = new Model();

                    // id
                    if (el.TryGetProperty("id", out var idEl))
                    {
                        var rawId = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(rawId))
                            model.Id = rawId.ToModelId(GetIdentifier());
                    }

                    // created
                    if (el.TryGetProperty("created", out var createdEl) &&
                        createdEl.ValueKind == JsonValueKind.Number)
                    {
                        model.Created = createdEl.GetInt64();
                    }

                    // owned_by
                    if (el.TryGetProperty("owned_by", out var ownedEl))
                        model.OwnedBy = ownedEl.GetString() ?? "";

                    if (!el.TryGetProperty("model_spec", out var spec) ||
                        spec.ValueKind != JsonValueKind.Object)
                        continue;

                    // name
                    if (spec.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Id;

                    // description
                    if (spec.TryGetProperty("description", out var descEl))
                        model.Description = descEl.GetString();

                    // context window
                    if (spec.TryGetProperty("availableContextTokens", out var ctxEl) &&
                        ctxEl.ValueKind == JsonValueKind.Number)
                    {
                        model.ContextWindow = ctxEl.GetInt32();
                    }

                    // max tokens
                    if (spec.TryGetProperty("maxCompletionTokens", out var maxEl) &&
                        maxEl.ValueKind == JsonValueKind.Number)
                    {
                        model.MaxTokens = maxEl.GetInt32();
                    }

                    // ------------------------
                    // Pricing
                    // ------------------------
                    if (spec.TryGetProperty("pricing", out var pricingEl) &&
                        pricingEl.ValueKind == JsonValueKind.Object)
                    {
                        decimal? input = null;
                        decimal? output = null;
                        decimal? cacheInput = null;

                        if (pricingEl.TryGetProperty("input", out var inputEl) &&
                            inputEl.TryGetProperty("usd", out var inputUsd))
                        {
                            input = inputUsd.GetDecimal();
                        }

                        if (pricingEl.TryGetProperty("output", out var outputEl) &&
                            outputEl.TryGetProperty("usd", out var outputUsd))
                        {
                            output = outputUsd.GetDecimal();
                        }

                        if (pricingEl.TryGetProperty("cache_input", out var cacheEl) &&
                            cacheEl.TryGetProperty("usd", out var cacheUsd))
                        {
                            cacheInput = cacheUsd.GetDecimal();
                        }

                        if (input.HasValue && output.HasValue)
                        {
                            model.Pricing = new ModelPricing
                            {
                                Input = input.Value,
                                Output = output.Value,
                                InputCacheRead = cacheInput
                            };
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(model.Id))
                        models.Add(model);
                }

                models.AddRange(await ListCharacterModels(cancellationToken));
                models.AddRange(GetIdentifier().GetModels());

                return models
                    .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                    .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(a => a.First());

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListCharacterModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ApplyAuthHeader();

        var models = new List<Model>();
        const int pageSize = 100;
        var offset = 0;

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/characters?limit={pageSize}&offset={offset}");
            using var resp = await _client.SendAsync(req, cancellationToken);

            if (!resp.IsSuccessStatusCode)
                return models;

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                break;

            var pageCount = 0;

            foreach (var el in dataEl.EnumerateArray())
            {
                pageCount++;

                var baseModelId = el.TryGetProperty("modelId", out var modelIdEl)
                    ? modelIdEl.GetString()
                    : null;

                var characterSlug = el.TryGetProperty("slug", out var slugEl)
                    ? slugEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(characterSlug))
                    continue;

                var characterName = el.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString()
                    : null;

                var description = el.TryGetProperty("description", out var descEl)
                    ? descEl.GetString()
                    : null;

                var tags = new List<string>
                {
                    "persona"
                };

                models.Add(new Model
                {
                    Id = $"{baseModelId}:character_slug={characterSlug}".ToModelId(GetIdentifier()),
                    Name = string.IsNullOrWhiteSpace(characterName) ? characterSlug : characterName,
                    Description = description,
                    Type = "language",
                    OwnedBy = nameof(Venice),
                    Tags = [.. tags.Distinct()]
                });
            }

            if (pageCount == 0)
                break;

            offset += pageSize;
        }

        return models;
    }
}
