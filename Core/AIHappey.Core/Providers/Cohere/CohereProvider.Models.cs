using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
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

                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models?page_size=1000");
                using var response = await _client.SendAsync(request, cancellationToken);

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("models", out var modelsEl)
                    || modelsEl.ValueKind != JsonValueKind.Array)
                    return [];

                var result = new List<Model>();

                foreach (var item in modelsEl.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    int? contextLength = null;
                    if (item.TryGetProperty("context_length", out var contextLengthEl))
                        contextLength = contextLengthEl.GetInt32();

                    IEnumerable<string>? tags = null;
                    if (item.TryGetProperty("features", out var featuresEl)
                        && featuresEl.ValueKind == JsonValueKind.Array)
                    {
                        tags = [.. featuresEl
                    .EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String)
                    .Select(f => f.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))];
                    }

                    result.Add(new Model
                    {
                        Id = name!.ToModelId(GetIdentifier()),
                        Name = name!,
                        Tags = tags,
                        ContextWindow = contextLength,
                        OwnedBy = nameof(Cohere)
                    });
                }

                return result;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    }
}