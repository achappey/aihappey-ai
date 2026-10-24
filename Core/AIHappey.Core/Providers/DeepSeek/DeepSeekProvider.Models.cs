using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var req = new HttpRequestMessage(HttpMethod.Get, "models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                var payload = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"DeepSeek API error: {payload}");

                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("DeepSeek model listing must contain a data array.");

                List<Model> models = [];

                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var ownedBy = item.TryGetProperty("owned_by", out var ownedEl) ? ownedEl.GetString() : null;

                    long? created = null;
                    if (item.TryGetProperty("created", out var createdEl)
                        && createdEl.ValueKind == JsonValueKind.Number
                        && createdEl.TryGetInt64(out var epoch))
                    {
                        created = epoch;
                    }

                    models.Add(new Model
                    {
                        Id = id!.ToModelId(GetIdentifier()),
                        Name = item.TryGetProperty("name", out var nameEl)
                            && nameEl.ValueKind == JsonValueKind.String
                                ? nameEl.GetString() ?? id
                                : id,

                                                ContextWindow = item.TryGetProperty("context_window", out var contextEl)
                            && contextEl.ValueKind == JsonValueKind.Number
                            && contextEl.TryGetInt32(out var context)
                                ? context
                                : null,

                                                MaxTokens = item.TryGetProperty("max_output_tokens", out var maxEl)
                            && maxEl.ValueKind == JsonValueKind.Number
                            && maxEl.TryGetInt32(out var max)
                                ? max
                                : null,
                        OwnedBy = ownedBy ?? "DeepSeek",
                        Created = created,
                        Type = id!.GuessModelType(),
                    });
                }

                models.AddRange(new[] { SearchModel, FetchModel }.Select(id => new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = id == SearchModel ? "DeepSeek Search" : "DeepSeek Fetch",
                    OwnedBy = "DeepSeek",
                    Type = "language",
                    Description = id == SearchModel
                        ? "Search the web using the last user message. Input price is USD per successful non-empty request, not per token."
                        : "Fetch one HTTP(S) URL from the current user input as Markdown. Input price is USD per successful non-empty request, not per token.",
                    Tags = [id == SearchModel ? "search" : "fetch"]
                }));

                return models
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .WithPricing(GetIdentifier())
                    .DistinctBy(m => m.Id)
                    .OrderByDescending(m => m.Created);

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    }
}

