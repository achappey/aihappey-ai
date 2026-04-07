using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<List<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                
                var response = await _client.GetAsync("v1/models", cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return [];

                return [.. data
            .EnumerateArray()
            .Select(m =>
            {
                var id = m.GetProperty("id").GetString() ?? string.Empty;
                var created = m.TryGetProperty("created", out var c) ? c.GetInt64() : 0;
                var ownedBy = m.TryGetProperty("owned_by", out var o) ? o.GetString() : string.Empty;

                int? contextWindow = m.TryGetProperty("context_window", out var v) &&
                                        v.ValueKind == JsonValueKind.Number
                                            ? v.GetInt32()
                                            : null;

                int? maxTokens = m.TryGetProperty("max_completion_tokens", out var z) &&
                                        z.ValueKind == JsonValueKind.Number
                                            ? z.GetInt32()
                                            : null;

                return new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = id,
                    ContextWindow = contextWindow,
                    MaxTokens = maxTokens,
                    OwnedBy = ownedBy!,
                    Created = created
                };
            })
            .OrderByDescending(r => r.Created)
            .DistinctBy(r => r.Id)];

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

}