using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
{
    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var models = new List<Model>();

                // Regular Telnyx AI models
                using (var req = new HttpRequestMessage(HttpMethod.Get, "ai/models"))
                using (var resp = await _client.SendAsync(req, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"Telnyx models failed ({(int)resp.StatusCode}): {body}");

                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in data.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idEl)
                                ? idEl.GetString()
                                : null;

                            if (string.IsNullOrWhiteSpace(id))
                                continue;

                            var fullId = id.ToModelId(GetIdentifier());

                            models.Add(new Model
                            {
                                Id = fullId,
                                Name = id,
                                OwnedBy = el.TryGetProperty("owned_by", out var ownedBy)
                                    ? ownedBy.GetString() ?? ""
                                    : "",
                                Created = el.TryGetProperty("created", out var created)
                                    && created.ValueKind == JsonValueKind.Number
                                        ? created.GetInt64()
                                        : null,
                                Type = fullId.GuessModelType()
                            });
                        }
                    }
                }

                // OpenAI-compatible embedding models
                using (var req = new HttpRequestMessage(
                           HttpMethod.Get,
                           "ai/openai/embeddings/models"))
                using (var resp = await _client.SendAsync(req, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"Telnyx embedding models failed ({(int)resp.StatusCode}): {body}");

                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in data.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idEl)
                                ? idEl.GetString()
                                : null;

                            if (string.IsNullOrWhiteSpace(id))
                                continue;

                            var fullId = id.ToModelId(GetIdentifier());

                            models.Add(new Model
                            {
                                Id = fullId,
                                Name = id,
                                OwnedBy = el.TryGetProperty("owned_by", out var ownedBy)
                                    ? ownedBy.GetString() ?? ""
                                    : "",
                                Created = el.TryGetProperty("created", out var created)
                                    && created.ValueKind == JsonValueKind.Number
                                        ? created.GetInt64()
                                        : null,
                                Type = "embedding"
                            });
                        }
                    }
                }

                models.AddRange(
                    await this.ListModels(
                        _keyResolver.Resolve(GetIdentifier())));

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}